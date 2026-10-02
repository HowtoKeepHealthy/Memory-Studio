using Iced.Intel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MemoryStudio;

[StructLayout(LayoutKind.Sequential)]
public struct NativeAccessTraceEvent
{
    public ulong Sequence, InstructionPointer, FaultAddress, TimestampMs;
    public uint ThreadId, AccessKind, Bitness, CodeSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public ulong[] Registers;
    public ulong Flags;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] CodeBytes;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public uint[] Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct AccessTraceState
{
    public uint Running, Attached, Bitness, Mode;
    public ulong TotalEvents, DroppedEvents, WatchAddress;
    public uint WatchSize, Status;
    public readonly bool IsRunning => Running != 0;
    public readonly bool IsAttached => Attached != 0;
}

[Flags]
public enum TracedMemoryAccess { Read = 1, Write = 2 }

public sealed record AccessTraceSample(ulong Sequence, ulong InstructionPointer, ulong FaultAddress,
    uint ThreadId, int Bitness, byte[] Bytes, string Instruction, TracedMemoryAccess Access,
    ulong MemoryAddress, int MemoryWidth, ulong[] Registers, ulong Flags, ulong TimestampMs,
    bool AddressFromRegisters)
{
    public string AccessText => Access == (TracedMemoryAccess.Read | TracedMemoryAccess.Write) ? "读取 / 写入" : Access == TracedMemoryAccess.Write ? "写入" : "读取";
}

public sealed class AccessTraceHit : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public AccessTraceSample Latest { get; private set; }
    public ulong Count { get; private set; } = 1;
    public ulong InstructionPointer => Latest.InstructionPointer;
    public uint ThreadId => Latest.ThreadId;
    public TracedMemoryAccess Access => Latest.Access;
    public string AddressText => $"0x{InstructionPointer:X16}";
    public string ThreadText => ThreadId.ToString();
    public string AccessText => Latest.AccessText;
    public string BytesText => string.Join(" ", Latest.Bytes.Select(b => b.ToString("X2")));
    public string Instruction => Latest.Instruction;
    public string CountText => Count.ToString("N0");
    public string MemoryRangeText => $"0x{Latest.MemoryAddress:X16} · {Latest.MemoryWidth} 字节";

    internal AccessTraceHit(AccessTraceSample sample) => Latest = sample;
    internal void Add(AccessTraceSample sample)
    {
        Latest = sample;
        ++Count;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

/// <summary>Owns a debugger trace session. Construction never attaches or changes target pages.</summary>
public sealed class AccessTraceService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(ulong Ip, uint Thread, TracedMemoryAccess Access), AccessTraceHit> _hits = new();
    private nint _session;
    private bool _disposed;
    private AccessTraceState _lastState;
    private const int MaximumDistinctHits = 10000;
    public int ProcessId { get; }
    public ulong Address { get; }
    public int Size { get; }
    public bool WritesOnly { get; }
    public ulong AcceptedEvents { get; private set; }
    public ulong FilteredEvents { get; private set; }
    public ulong UnresolvedEvents { get; private set; }
    public ulong SuppressedEvents { get; private set; }
    public string LastError { get; private set; } = "";
    public IReadOnlyList<AccessTraceHit> Hits { get { lock (_gate) return _hits.Values.ToArray(); } }

    public AccessTraceService(int processId, ulong address, int size, bool writesOnly)
    {
        if (processId <= 0 || processId == Environment.ProcessId) throw new ArgumentException("请选择其他进程进行访问追踪；不能调试当前 Memory Studio 进程。");
        if (size is < 1 or > 4096 || address == 0 || address > ulong.MaxValue - (ulong)size) throw new ArgumentException("追踪地址必须有效，范围长度须为 1 至 4,096 字节。");
        ProcessId = processId; Address = address; Size = size; WritesOnly = writesOnly;
    }

    public AccessTraceState State
    {
        get
        {
            lock (_gate)
            {
                if (_session != 0) Native.ms_trace_get_state(_session, out _lastState);
                return _lastState;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session != 0) throw new InvalidOperationException("该追踪已经启动；请先停止。");
            if (Marshal.SizeOf<NativeAccessTraceEvent>() != 208 || Marshal.SizeOf<AccessTraceState>() != 48)
                throw new InvalidOperationException("访问追踪的原生接口布局不匹配。");
            _session = Native.ms_trace_start((uint)ProcessId, Address, (uint)Size, WritesOnly ? 1u : 0u);
            if (_session == 0) { LastError = ReadError(0); throw new InvalidOperationException(LastError); }
            Native.ms_trace_get_state(_session, out _lastState);
            if (!_lastState.IsRunning || !_lastState.IsAttached || _lastState.Status != 0)
            {
                // Native startup can return a recovery session when attachment
                // failed and cleanup also failed. Retain ownership for Stop retry.
                LastError = ReadError(_session);
                if (_lastState.IsAttached) LastError += " 目标仍处于调试附加状态，请使用停止按钮重试恢复。";
                throw new InvalidOperationException(LastError);
            }
            _hits.Clear(); AcceptedEvents = FilteredEvents = UnresolvedEvents = SuppressedEvents = 0;
            LastError = "";
        }
    }

    /// <summary>Drain a bounded event batch and return only newly created aggregate rows.</summary>
    public IReadOnlyList<AccessTraceHit> Poll()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return PollCore();
        }
    }

    private IReadOnlyList<AccessTraceHit> PollCore()
    {
        var added = new List<AccessTraceHit>();
        if (_session == 0) return added;
        var events = new NativeAccessTraceEvent[256];
        for (int batch = 0; batch < 4; ++batch)
        {
            uint count = Native.ms_trace_poll(_session, events, (uint)events.Length);
            for (int i = 0; i < count; ++i)
            {
                if (!TryDecodeHit(events[i], Address, Size, WritesOnly, out var sample))
                {
                    if (events[i].CodeSize == 0 || events[i].Bitness is not (32 or 64)) ++UnresolvedEvents;
                    else ++FilteredEvents;
                    continue;
                }
                ++AcceptedEvents;
                var key = (sample!.InstructionPointer, sample.ThreadId, sample.Access);
                if (_hits.TryGetValue(key, out var existing)) existing.Add(sample);
                else if (_hits.Count < MaximumDistinctHits)
                {
                    var hit = new AccessTraceHit(sample);
                    _hits.Add(key, hit); added.Add(hit);
                }
                else ++SuppressedEvents;
            }
            if (count < events.Length) break;
        }
        Native.ms_trace_get_state(_session, out _lastState);
        if (_lastState.Status != 0) LastError = ReadError(_session);
        return added;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_session == 0) return;
            int status = Native.ms_trace_stop(_session);
            try
            {
                // Stop has completed one cleanup attempt. Drain queued candidates;
                // only a confirmed detach below permits closing the native session.
                for (int pass = 0; pass < 16; ++pass)
                {
                    ulong previous = AcceptedEvents + FilteredEvents + UnresolvedEvents;
                    PollCore();
                    if (AcceptedEvents + FilteredEvents + UnresolvedEvents == previous) break;
                }
                Native.ms_trace_get_state(_session, out _lastState);
                if (status != 0) LastError = ReadError(_session);
            }
            finally
            {
                Native.ms_trace_get_state(_session, out _lastState);
                // Keep ownership if native cleanup reports a remaining attachment.
                // Discarding the handle here would conceal a debugger left on target.
                if (!_lastState.IsAttached) { Native.ms_trace_close(_session); _session = 0; }
            }
            if (_session != 0)
                throw new InvalidOperationException("目标仍处于调试附加状态，停止尚未完成。请重试停止。" + LastError);
            if (status != 0) throw new InvalidOperationException(LastError);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { Stop(); }
            finally { _disposed = _session == 0; }
        }
    }

    /// <summary>Reject page-level noise by checking the decoded operand's actual address and width.</summary>
    public static bool TryDecodeHit(NativeAccessTraceEvent raw, ulong watchAddress, int watchSize,
        bool writesOnly, out AccessTraceSample? sample)
    {
        sample = null;
        if (watchSize <= 0 || watchAddress > ulong.MaxValue - (ulong)watchSize || raw.AccessKind is not (0 or 1) || (writesOnly && raw.AccessKind != 1)) return false;
        if (raw.Bitness is not (32 or 64) || raw.CodeSize is < 1 or > 15 || raw.CodeBytes == null || raw.CodeBytes.Length < raw.CodeSize || raw.Registers == null || raw.Registers.Length < 16) return false;
        var decoder = Iced.Intel.Decoder.Create((int)raw.Bitness, new ByteArrayCodeReader(raw.CodeBytes.AsSpan(0, (int)raw.CodeSize).ToArray()), raw.InstructionPointer);
        decoder.Decode(out var instruction);
        if (instruction.IsInvalid || decoder.LastError != DecoderError.None || instruction.Length <= 0) return false;
        var factory = new InstructionInfoFactory();
        var info = factory.GetInfo(in instruction);
        var memory = new List<UsedMemory>();
        foreach (var operand in info.GetUsedMemory()) memory.Add(operand);
        foreach (var operand in memory)
        {
            bool reads = operand.Access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or OpAccess.ReadCondWrite;
            bool writes = operand.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;
            if ((raw.AccessKind == 0 && !reads) || (raw.AccessKind == 1 && !writes)) continue;
            int width = operand.MemorySize.GetSize();
            if (width <= 0) continue;
            var provider = new RegisterProvider(raw, instruction.Length);
            bool computed = operand.TryGetVirtualAddress(0, provider, out ulong effective);
            if (!computed)
            {
                // The ABI does not contain segment bases or SIMD lanes. The OS still
                // gives an exact access address, but avoid ambiguity with two operands.
                if (memory.Count != 1) continue;
                effective = raw.FaultAddress;
            }
            if (effective > ulong.MaxValue - (ulong)width || raw.FaultAddress < effective || raw.FaultAddress - effective >= (ulong)width) continue;
            if (effective >= watchAddress + (ulong)watchSize || watchAddress >= effective + (ulong)width) continue;
            var formatter = new IntelFormatter();
            formatter.Options.HexPrefix = "0x"; formatter.Options.HexSuffix = "";
            formatter.Options.UppercaseHex = true; formatter.Options.SpaceAfterOperandSeparator = true;
            var output = new StringOutput(); formatter.Format(in instruction, output);
            var access = (reads ? TracedMemoryAccess.Read : 0) | (writes ? TracedMemoryAccess.Write : 0);
            sample = new AccessTraceSample(raw.Sequence, raw.InstructionPointer, raw.FaultAddress, raw.ThreadId,
                (int)raw.Bitness, raw.CodeBytes.AsSpan(0, instruction.Length).ToArray(), output.ToStringAndReset(),
                access, effective, width, raw.Registers.ToArray(), raw.Flags, raw.TimestampMs, computed);
            return true;
        }
        return false;
    }

    private sealed class RegisterProvider(NativeAccessTraceEvent raw, int instructionLength) : IVATryGetRegisterValueProvider
    {
        public bool TryGetRegisterValue(Register register, int elementIndex, int elementSize, out ulong value)
        {
            value = 0;
            if (register is Register.None or Register.ES or Register.CS or Register.SS or Register.DS) return true;
            if (register is Register.FS or Register.GS) return false;
            if (register is Register.RIP or Register.EIP)
            {
                value = raw.InstructionPointer + (ulong)instructionLength;
                if (register == Register.EIP) value = (uint)value;
                return true;
            }
            Register full = register.GetFullRegister();
            int index = full switch
            {
                Register.RAX => 0, Register.RCX => 1, Register.RDX => 2, Register.RBX => 3,
                Register.RSP => 4, Register.RBP => 5, Register.RSI => 6, Register.RDI => 7,
                Register.R8 => 8, Register.R9 => 9, Register.R10 => 10, Register.R11 => 11,
                Register.R12 => 12, Register.R13 => 13, Register.R14 => 14, Register.R15 => 15,
                _ => -1
            };
            if (index < 0) return false;
            value = raw.Registers[index];
            if (register is Register.AH or Register.CH or Register.DH or Register.BH) value >>= 8;
            int width = register.GetSize();
            if (width is > 0 and < 8) value &= (1UL << (width * 8)) - 1;
            return true;
        }
    }

    private static string ReadError(nint session)
    {
        var buffer = new byte[2048];
        Native.ms_trace_error(session, buffer, (uint)buffer.Length);
        int length = Array.IndexOf(buffer, (byte)0);
        string error = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        return string.IsNullOrWhiteSpace(error) ? "访问追踪未能完成。请检查目标是否仍在运行、地址权限及是否已被其他调试器附加。" : error;
    }

    private static class Native
    {
        private const string Dll = "memory_core.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint ms_trace_start(uint pid, ulong address, uint size, uint mode);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint ms_trace_poll(nint session, [Out] NativeAccessTraceEvent[] events, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_trace_get_state(nint session, out AccessTraceState state);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_trace_stop(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_trace_close(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint ms_trace_error(nint session, [Out] byte[] buffer, uint capacity);
    }
}
