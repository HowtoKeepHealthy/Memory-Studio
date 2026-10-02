using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MemoryStudio;

internal static class FeatureSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        byte[] x64 = [0x55, 0x48, 0x89, 0xE5, 0x48, 0x83, 0xEC, 0x20, 0xE8, 0x05, 0, 0, 0, 0xC3];
        var rows = DisassemblyService.Decode(x64, 0x1000, 64);
        check(rows.Count == 5 && rows[1].Address == 0x1001 && rows[1].Length == 3 && rows[1].Instruction.Contains("rbp"), "x64 instruction lengths, addresses and operands");
        check(rows[3].BranchTarget == 0x1012, "Relative CALL destination");
        check(rows.SelectMany(r => r.Bytes).SequenceEqual(x64), "Instruction bytes preserve the input snapshot");
        byte[] modeSensitive = [0x48, 0x89, 0xC8];
        var x86Rows = DisassemblyService.Decode(modeSensitive, 0x2000, 32);
        var x64Rows = DisassemblyService.Decode(modeSensitive, 0x2000, 64);
        check(x86Rows.Count == 2 && x86Rows[0].Instruction.StartsWith("dec") && x64Rows.Count == 1 && x64Rows[0].Instruction.Contains("rax"), "x86 and x64 decode modes differ correctly");
        var truncated = DisassemblyService.Decode([0x0F], 0x3000, 64);
        check(truncated.Count == 1 && truncated[0].IsTruncated && truncated[0].IsInvalid, "Truncated instruction is explicitly marked");
        check(DisassemblyService.Decode([], 0x3000, 64).Count == 0, "Empty disassembly input");

        using var engine = new NativeEngine(Environment.ProcessId);
        var inspection = ProcessInspector.Inspect(Environment.ProcessId, engine);
        check(inspection.Architecture.Bitness == 64 && inspection.Modules.Any(m => m.EntryPoint != null), "Real process bitness and module entry points");
        var executable = inspection.Modules.First(m => m.EntryPoint != null);
        var code = DisassemblyService.ReadWindow(engine, executable.EntryPoint!.Value, 64, 64);
        check(code.Rows.Count > 0 && code.Rows[0].Address == executable.EntryPoint && code.ReadBytes >= 64, "Disassemble a real loaded module entry point");

        nint allocation = VirtualAlloc(0, (nuint)(Environment.SystemPageSize * 2), 0x3000, 0x04);
        check(allocation != 0, "Allocate boundary test pages");
        try
        {
            nint boundary = allocation + Environment.SystemPageSize;
            byte[] jump = [0xE9, 0, 0, 0, 0];
            Marshal.Copy(jump, 0, boundary - jump.Length, jump.Length);
            check(VirtualProtect(boundary, (nuint)Environment.SystemPageSize, 0x01, out _), "Protect unreadable boundary page");
            var partial = DisassemblyService.ReadWindow(engine, (ulong)(boundary - jump.Length), 64, 64);
            check(partial.ReadBytes == 5 && partial.Rows.Count == 1 && partial.BoundaryMessage != null, "Disassembly stops at unreadable memory without joining gaps");
        }
        finally { if (allocation != 0) VirtualFree(allocation, 0, 0x8000); }

        var probe = new Window { Title = "Memory Studio picker probe", ShowActivated = false, ShowInTaskbar = false, Width = 160, Height = 100, WindowStartupLocation = WindowStartupLocation.Manual, Left = -15000, Top = -15000 };
        nint child = 0;
        try
        {
            probe.Show();
            nint hwnd = new WindowInteropHelper(probe).Handle;
            check(WindowProcessPicker.ResolveWindow(0) == null, "Picker rejects invalid window");
            check(WindowProcessPicker.ResolveWindow(hwnd) == null, "Picker excludes its own process");
            var target = WindowProcessPicker.ResolveWindow(hwnd, true);
            check(target?.ProcessId == Environment.ProcessId && target.Handle == hwnd, "Picker resolves actual window owner PID");
            child = CreateWindowEx(0, "STATIC", "probe child", 0x50000000, 0, 0, 40, 30, hwnd, 0, 0, 0);
            check(child != 0 && WindowProcessPicker.ResolveWindow(child, true)?.Handle == hwnd, "Picker resolves child controls to the top-level window");
            check(WindowProcessPicker.ResolveWindow(GetDesktopWindow(), true) == null, "Picker excludes the desktop");
        }
        finally { if (child != 0) DestroyWindow(child); probe.Close(); }

        string demo = BundledAssets.Resolve("DemoTarget.exe");
        byte[] header = new byte[2];
        using (var file = File.OpenRead(demo)) check(file.Read(header, 0, 2) == 2 && header[0] == 'M' && header[1] == 'Z', "Bundled demo extracts as a valid Windows executable");
        using var demoProcess = Process.Start(new ProcessStartInfo(demo, "--self-test") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(demo)! }) ?? throw new InvalidOperationException("Demo self-test did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await demoProcess.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { demoProcess.Kill(); throw new TimeoutException("Bundled demo self-test timed out."); }
        check(demoProcess.ExitCode == 0, "Bundled demo runs without sidecar dependencies");
        await TraceChildAsync(demo, check);
    }

    private static async Task TraceChildAsync(string demo, Action<bool, string> check)
    {
        using var child = Process.Start(new ProcessStartInfo(demo, "--trace-test")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(demo)!
        }) ?? throw new InvalidOperationException("Trace test child did not start.");
        try
        {
            using var headerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string[] header = (await child.StandardOutput.ReadLineAsync(headerTimeout.Token) ?? "").Split(' ');
            string[] code = (await child.StandardOutput.ReadLineAsync(headerTimeout.Token) ?? "").Split(' ');
            check(header.Length == 3 && header[0] == "TRACE" && int.Parse(header[1]) == child.Id, "Trace child exposes its own PID and isolated watched page");
            ulong address = Convert.ToUInt64(header[2], 16);
            ulong readIp = Convert.ToUInt64(code[1].Split('=')[1], 16);
            ulong writeIp = Convert.ToUInt64(code[2].Split('=')[1], 16);
            ulong neighbourIp = Convert.ToUInt64(code[3].Split('=')[1], 16);
            using var reader = new NativeEngine(child.Id);
            using (var pause = reader.PauseMemoryAccess())
            {
                bool blocked = false;
                try { reader.Read(address, 4); } catch (InvalidOperationException) { blocked = true; }
                check(blocked, "Ordinary memory reads are paused during source tracing");
            }
            check(reader.Read(address, 4).Length == 4, "Memory reads resume after the trace pause is released");
            foreach (bool writesOnly in new[] { false, true })
            {
                using var trace = new AccessTraceService(child.Id, address, 4, writesOnly);
                await Task.Run(trace.Start);
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 3500)
                {
                    trace.Poll();
                    if (!trace.State.IsRunning) throw new InvalidOperationException($"Trace stopped unexpectedly: {trace.LastError}");
                    if (trace.AcceptedEvents >= 8 && (writesOnly || trace.FilteredEvents >= 4)) break;
                    await Task.Delay(30);
                }
                await Task.Run(trace.Stop);
                check(!trace.State.IsAttached && !trace.State.IsRunning, $"{(writesOnly ? "Write" : "Access")} trace detaches cleanly");
                check(trace.Hits.Any(h => (h.Access & TracedMemoryAccess.Write) != 0 && h.InstructionPointer >= writeIp && h.InstructionPointer < writeIp + 16), "Real trace identifies the exact writing instruction");
                check(trace.Hits.All(h => h.InstructionPointer < neighbourIp || h.InstructionPointer >= neighbourIp + 24), "Real trace excludes reads and writes to a neighbour on the same page");
                if (writesOnly)
                    check(trace.Hits.All(h => (h.Access & TracedMemoryAccess.Write) != 0), "Write-only trace filters out pure reads after reattachment");
                else
                {
                    check(trace.Hits.Any(h => h.Access == TracedMemoryAccess.Read && h.InstructionPointer >= readIp && h.InstructionPointer < readIp + 16), "Real trace identifies the exact reading instruction");
                    check(trace.FilteredEvents > 0 && trace.Hits.Any(h => h.Count > 1), "Trace filters page noise and aggregates repeated instructions");
                }
                check(VirtualQueryEx(child.Handle, (nint)address, out var page, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0 && page.Protect == 0x04, "Stopping restores the original page protection");
                int before = BitConverter.ToInt32(reader.Read(address, 4));
                await Task.Delay(180);
                int after = BitConverter.ToInt32(reader.Read(address, 4));
                check(!child.HasExited && after > before, "Target continues executing and changing values after debugger detach");
            }
            using (var trace = new AccessTraceService(child.Id, address, 4, false))
            {
                await Task.Run(trace.Start);
                child.Kill();
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(exitTimeout.Token);
                var exitTimer = Stopwatch.StartNew();
                while (trace.State.IsRunning && exitTimer.ElapsedMilliseconds < 3000) await Task.Delay(20);
                try { await Task.Run(trace.Stop); } catch (InvalidOperationException) { /* Target exit is reported as a trace error. */ }
                check(!trace.State.IsAttached && !trace.State.IsRunning, "Target exit releases the debugger and stops the trace session");
            }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation information, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
}
