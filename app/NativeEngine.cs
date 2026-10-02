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
public sealed class NativeEngine : IDisposable
{
    private nint _session;
    private readonly object _gate = new();
    private readonly object _lifetimeGate = new();
    private string _lastScanError = "";
    public NativeEngine(int pid)
    {
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
    public int Scan(ScanRequest request, byte[] value, bool next)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var pin = GCHandle.Alloc(value.Length == 0 ? new byte[1] : value, GCHandleType.Pinned);
            try
            {
                request.Value = pin.AddrOfPinnedObject(); request.ValueSize = (uint)value.Length;
                int status = Native.ms_scan(_session, in request, next ? 1u : 0u);
                _lastScanError = status != 0 ? GetErrorCore() : "";
                return status;
            }
            finally { pin.Free(); }
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
            var addresses = new ulong[capacity];
            uint count = Native.ms_get_results(_session, offset, addresses, capacity);
            var rows = new List<ResultRow>((int)count);
            for (int i = 0; i < count; i++)
            {
                var bytes = new byte[size];
                bool ok = Native.ms_read(_session, addresses[i], bytes, (uint)size) == 0;
                rows.Add(new ResultRow(addresses[i], ok ? bytes : [], ok ? ValueCodec.Format(type, bytes) : "不可读取", ValueCodec.TypeLabel(type)));
            }
            return rows;
        }
    }
    public byte[] Read(ulong address, int size)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
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
            if (Native.ms_write(_session, address, bytes, (uint)bytes.Length) != 0) throw new InvalidOperationException(Error());
        }
    }
    public void Dispose()
    {
        Cancel();
        lock (_gate) { lock (_lifetimeGate) { if (_session == 0) return; Native.ms_close(_session); _session = 0; } }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_session == 0, this);
    private static class Native
    {
        private const string Dll = "memory_core.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint ms_open(uint pid);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_close(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_scan(nint session, in ScanRequest request, uint next);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_cancel(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void ms_get_progress(nint session, out ScanProgressInfo progress);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ms_result_count(nint session);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint ms_get_results(nint session, ulong offset, [Out] ulong[] addresses, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_read(nint session, ulong address, [Out] byte[] bytes, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int ms_write(nint session, ulong address, byte[] bytes, uint size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint ms_error(nint session, [Out] byte[] bytes, uint size);
    }
}
