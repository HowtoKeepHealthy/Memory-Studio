using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MemoryStudio;

/// <summary>
/// Suspends target threads through the documented Win32 API. Owns exactly one
/// suspension increment per handle, preserving other debuggers' suspend counts.
/// Dispose and normal application exit restore owned increments. Forced controller
/// termination cannot run cleanup, so callers must resume before abandoning a session.
/// </summary>
public sealed class ProcessControlService : IDisposable
{
    private static readonly object RegistryGate = new();
    private static readonly HashSet<ProcessControlService> Active = [];
    private readonly object _gate = new();
    private sealed class ThreadHold(SafeWaitHandle handle) { public SafeWaitHandle Handle { get; } = handle; public bool Owned; }
    private readonly Dictionary<uint, ThreadHold> _threads = new();
    private readonly SafeWaitHandle _process;
    private Timer? _watcher;
    private bool _paused, _disposed, _wow64;
    public int ProcessId { get; }
    public string LastError { get; private set; } = "";

    static ProcessControlService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            ProcessControlService[] sessions;
            lock (RegistryGate) sessions = Active.ToArray();
            foreach (var session in sessions) { try { session.Resume(); } catch { } }
        };
    }
    public ProcessControlService(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) throw new ArgumentException("进程暂停只能用于其他进程。", nameof(pid));
        ProcessId = pid;
        _process = Native.OpenProcess(0x00101000, false, (uint)pid); // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION.
        if (_process.IsInvalid) { int error = Marshal.GetLastWin32Error(); _process.Dispose(); throw new Win32Exception(error, "无法打开要暂停的目标进程。"); }
        if (!Native.IsWow64Process(_process, out _wow64)) { int error = Marshal.GetLastWin32Error(); _process.Dispose(); throw new Win32Exception(error); }
        if (!Alive()) { _process.Dispose(); throw new InvalidOperationException("目标进程已经退出。"); }
    }
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (!Alive()) ClearExited();
                return _paused || _threads.Count != 0;
            }
        }
    }
    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Alive()) { ClearExited(); throw new InvalidOperationException("目标进程已经退出。"); }
            if (_paused) return;
            try
            {
                CaptureUntilStable();
                _paused = true; LastError = "";
                lock (RegistryGate) Active.Add(this);
                _watcher ??= new Timer(_ => Watch(), null, 100, 100);
            }
            catch (Exception error)
            {
                LastError = error.Message;
                try { ResumeCore(); }
                catch (Exception cleanup) { LastError += " 恢复暂停时也失败：" + cleanup.Message; lock (RegistryGate) Active.Add(this); }
                throw new InvalidOperationException(LastError, error);
            }
        }
    }
    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ResumeCore(); LastError = "";
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Keep ownership and allow retry if any live thread could not be resumed.
            ResumeCore(); _process.Dispose(); _disposed = true;
        }
    }
    private bool Alive() => Native.WaitForSingleObject(_process, 0) == 258;
    private void ClearExited()
    {
        foreach (var thread in _threads.Values) thread.Handle.Dispose();
        _threads.Clear(); _paused = false; _watcher?.Dispose(); _watcher = null;
        lock (RegistryGate) Active.Remove(this);
    }
    private void ResumeCore()
    {
        _watcher?.Dispose(); _watcher = null;
        int firstError = 0;
        foreach (var pair in _threads.ToArray())
        {
            bool exited = Native.WaitForSingleObject(pair.Value.Handle, 0) == 0;
            if (pair.Value.Owned && !exited && Native.ResumeThread(pair.Value.Handle) == uint.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                if (Native.WaitForSingleObject(pair.Value.Handle, 0) != 0) { if (firstError == 0) firstError = error; continue; }
            }
            pair.Value.Handle.Dispose(); _threads.Remove(pair.Key);
        }
        _paused = _threads.Count != 0;
        if (!_paused) { lock (RegistryGate) Active.Remove(this); }
        else { LastError = new Win32Exception(firstError).Message; throw new Win32Exception(firstError, "部分线程尚未恢复，请重试恢复：" + LastError); }
    }
    private void Watch()
    {
        lock (_gate)
        {
            if (_disposed || !_paused) return;
            if (!Alive()) { ClearExited(); return; }
            try { CaptureUntilStable(); }
            catch (Exception error)
            {
                LastError = "维护暂停状态失败，正在恢复：" + error.Message;
                try { ResumeCore(); } catch (Exception cleanup) { LastError += " " + cleanup.Message; }
            }
        }
    }
    private void CaptureUntilStable()
    {
        int stablePasses = 0;
        for (int pass = 0; pass < 12; ++pass)
        {
            int added = CaptureThreads();
            stablePasses = added == 0 ? stablePasses + 1 : 0;
            if (stablePasses >= 2) return;
        }
        throw new InvalidOperationException("目标持续创建线程，无法稳定暂停。已取消本次暂停。");
    }
    private int CaptureThreads()
    {
        int added = 0;
        using var snapshot = Native.CreateToolhelp32Snapshot(4, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举目标线程。");
        var entry = new ThreadEntry { Size = (uint)Marshal.SizeOf<ThreadEntry>() };
        if (!Native.Thread32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取线程列表。");
        do
        {
            if (entry.OwnerProcessId != (uint)ProcessId) continue;
            if (_threads.TryGetValue(entry.ThreadId, out var known))
            {
                if (Native.WaitForSingleObject(known.Handle, 0) != 0) continue;
                known.Handle.Dispose(); _threads.Remove(entry.ThreadId); // A reused TID requires a new owned handle.
            }
            var handle = Native.OpenThread(0x00100802, false, entry.ThreadId); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION | SUSPEND_RESUME.
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle.Dispose();
                if (error == 87) continue; // Thread exited between census and OpenThread.
                throw new Win32Exception(error, "无法暂停目标线程。");
            }
            uint owner = Native.GetProcessIdOfThread(handle);
            if (owner == 0) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法核对线程所属进程。"); }
            if (owner != (uint)ProcessId) { handle.Dispose(); continue; }
            var hold = new ThreadHold(handle);
            try
            {
                // Reserve ownership before SuspendThread so allocation failure cannot
                // strand an unrecorded suspend increment on the target.
                _threads.Add(entry.ThreadId, hold);
                uint count = _wow64 ? Native.Wow64SuspendThread(handle) : Native.SuspendThread(handle);
                if (count == uint.MaxValue)
                {
                    int error = Marshal.GetLastWin32Error();
                    _threads.Remove(entry.ThreadId);
                    if (Native.WaitForSingleObject(handle, 0) == 0) continue;
                    throw new Win32Exception(error, "暂停目标线程失败。");
                }
                hold.Owned = true;
                handle = null!; ++added;
            }
            finally
            {
                if (handle is not null) { _threads.Remove(entry.ThreadId); handle.Dispose(); }
            }
        } while (Native.Thread32Next(snapshot, ref entry));
        int last = Marshal.GetLastWin32Error();
        if (last != 18) throw new Win32Exception(last, "目标线程枚举未完成。");
        return added;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ThreadEntry
    {
        public uint Size, Usage, ThreadId, OwnerProcessId;
        public int BasePriority, DeltaPriority;
        public uint Flags;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool IsWow64Process(SafeWaitHandle process, out bool wow64);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool Thread32First(SafeWaitHandle snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool Thread32Next(SafeWaitHandle snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeWaitHandle OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessIdOfThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint SuspendThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint Wow64SuspendThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeWaitHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}
