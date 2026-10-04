using System.Runtime.InteropServices;
using System.Text;

namespace MemoryStudio;

[StructLayout(LayoutKind.Sequential)]
public struct ScanRequest
{
    public uint Type, Mode;
    public ulong StartAddress, EndAddress;
    public nint Value;
    public uint ValueSize, Alignment;
    public ulong MaxResults;
    public uint WritableOnly, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
public struct ScanProgressInfo
{
    public ulong ScannedBytes, TotalBytes, ResultCount;
    public uint Running, Cancelled;
    public double ElapsedMs;
}
public sealed class ScanSettings
{
    public bool Approximate { get; set; }
    public double AbsoluteTolerance { get; set; }
    public double RelativeTolerance { get; set; }
    public byte[]? UpperValue { get; set; }
    public byte[]? PatternMask { get; set; }
}
[StructLayout(LayoutKind.Sequential)]
public struct ScanHistoryInfo
{
    public uint UndoCount, MaxSteps, Type, ByteWidth;
    private uint _hasScan, _reserved;
    public ulong UsedBytes, BudgetBytes, Generation;
    public readonly bool HasScan => _hasScan != 0;
    public readonly bool CanUndo => UndoCount > 0;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeScanOptions
{
    public uint StructSize, Flags;
    public double AbsoluteTolerance, RelativeTolerance;
    public nint UpperValue;
    public uint UpperValueSize, Reserved;
    public nint PatternMask;
    public uint PatternMaskSize, Reserved2;
}
public sealed class NativeEngine : IDisposable
{
    private nint _session;
    private readonly object _gate = new();
    private readonly object _lifetimeGate = new();
    private string _lastScanError = "";
    private int _memoryPauseCount;
    public int ProcessId { get; }
    public NativeEngine(int pid)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        ProcessId = pid;
        _session = Native.ms_open((uint)pid);
        if (_session == 0) throw new InvalidOperationException(Error());
    }
    public string Error()
    {
        lock (_gate) return GetErrorCore();
    }
    public string LastScanError { get { lock (_gate) return _lastScanError; } }
    private string GetErrorCore()
    {
        byte[] buffer = new byte[2048];
        Native.ms_error(_session, buffer, (uint)buffer.Length);
        int length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }
    public int Scan(ScanRequest request, byte[] value, bool next, ScanSettings? settings = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfMemoryAccessPaused();
            var pin = GCHandle.Alloc(value.Length == 0 ? new byte[1] : value, GCHandleType.Pinned);
            GCHandle upperPin = default, maskPin = default;
            try
            {
                request.Value = pin.AddrOfPinnedObject(); request.ValueSize = (uint)value.Length;
                int status;
                if (settings is null) status = Native.ms_scan(_session, in request, next ? 1u : 0u);
                else
                {
                    var options = new NativeScanOptions
                    {
                        StructSize = (uint)Marshal.SizeOf<NativeScanOptions>(), Flags = settings.Approximate ? 1u : 0u,
                        AbsoluteTolerance = settings.AbsoluteTolerance, RelativeTolerance = settings.RelativeTolerance,
                    };
                    if (settings.UpperValue is { Length: > 0 } upper)
                    {
                        upperPin = GCHandle.Alloc(upper, GCHandleType.Pinned);
                        options.UpperValue = upperPin.AddrOfPinnedObject(); options.UpperValueSize = (uint)upper.Length;
                    }
                    if (settings.PatternMask is { Length: > 0 } mask)
                    {
                        maskPin = GCHandle.Alloc(mask, GCHandleType.Pinned);
                        options.PatternMask = maskPin.AddrOfPinnedObject(); options.PatternMaskSize = (uint)mask.Length;
                    }
                    status = Native.ms_scan_ex(_session, in request, next ? 1u : 0u, in options);
                }
                _lastScanError = status != 0 ? GetErrorCore() : "";
                return status;
            }
            finally { if (maskPin.IsAllocated) maskPin.Free(); if (upperPin.IsAllocated) upperPin.Free(); pin.Free(); }
        }
    }
    public ScanHistoryInfo History
    {
        get { lock (_gate) { ThrowIfDisposed(); Native.ms_get_scan_history(_session, out var info); return info; } }
    }
    public int UndoScan()
    {
        lock (_gate)
        {
            ThrowIfDisposed(); ThrowIfMemoryAccessPaused();
            int status = Native.ms_undo_scan(_session);
            _lastScanError = status != 0 ? GetErrorCore() : "";
            return status;
        }
    }
    public void Cancel() { lock (_lifetimeGate) { if (_session != 0) Native.ms_cancel(_session); } }
    public ScanProgressInfo Progress() { lock (_lifetimeGate) { if (_session == 0) return default; Native.ms_get_progress(_session, out var value); return value; } }
    public ulong Count { get { lock (_gate) { ThrowIfDisposed(); return Native.ms_result_count(_session); } } }
    public IReadOnlyList<ResultRow> Page(ulong offset, uint capacity, int type, int size)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfMemoryAccessPaused();
            var addresses = new ulong[capacity];
            uint count = Native.ms_get_results(_session, offset, addresses, capacity);
            var rows = new List<ResultRow>((int)count);
            for (int i = 0; i < count; i++)
            {
                var bytes = new byte[size];
                bool ok = Native.ms_read(_session, addresses[i], bytes, (uint)size) == 0;
                rows.Add(new ResultRow(addresses[i], ok ? bytes : [], ok ? ValueCodec.Format(type, bytes) : "不可读取", ValueCodec.TypeLabel(type), type, size));
            }
            return rows;
        }
    }
    public byte[] Read(ulong address, int size)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfMemoryAccessPaused();
            var bytes = new byte[size];
            int status = Native.ms_read(_session, address, bytes, (uint)size);
            if (status != 0) throw new InvalidOperationException(Error());
            return bytes;
        }
    }
    public void Write(ulong address, byte[] bytes)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfMemoryAccessPaused();
            if (Native.ms_write(_session, address, bytes, (uint)bytes.Length) != 0) throw new InvalidOperationException(Error());
        }
    }
    public void WriteCode(ulong address, byte[] bytes)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); ThrowIfMemoryAccessPaused();
            if (Native.ms_write_code(_session, address, bytes, (uint)bytes.Length) != 0) throw new InvalidOperationException(GetErrorCore());
        }
    }
    public void Dispose()
    {
        Cancel();
        lock (_gate) { lock (_lifetimeGate) { if (_session == 0) return; Native.ms_close(_session); _session = 0; } }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_session == 0, this);
    private void ThrowIfMemoryAccessPaused()
    {
        if (_memoryPauseCount != 0) throw new InvalidOperationException("来源追踪期间已暂停普通内存读写。关闭追踪窗口后即可继续。");
    }
    public IDisposable PauseMemoryAccess()
    {
        lock (_gate) { ThrowIfDisposed(); ++_memoryPauseCount; }
        return new AccessPause(this);
    }
    private sealed class AccessPause(NativeEngine engine) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { lock (engine._gate) { if (_disposed) return; _disposed = true; --engine._memoryPauseCount; } }
    }
    private static class Native
    {
        private const string Dll = "memory_core.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint ms_open(uint pid);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_close(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_scan(nint session, in ScanRequest request, uint next);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_scan_ex(nint session, in ScanRequest request, uint next, in NativeScanOptions options);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_undo_scan(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_get_scan_history(nint session, out ScanHistoryInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_cancel(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_get_progress(nint session, out ScanProgressInfo progress);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ms_result_count(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint ms_get_results(nint session, ulong offset, [Out] ulong[] addresses, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_read(nint session, ulong address, [Out] byte[] bytes, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_write(nint session, ulong address, byte[] bytes, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_write_code(nint session, ulong address, byte[] bytes, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint ms_error(nint session, [Out] byte[] bytes, uint size);
    }
}
