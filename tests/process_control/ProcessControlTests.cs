using MemoryStudio;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

// A normal helper exit exercises the production ProcessExit cleanup path.
if (args is ["--pause-helper", var pidText])
{
    var owner = new ProcessControlService(int.Parse(pidText));
    owner.Pause();
    Console.WriteLine("PAUSED");
    return;
}
if (args.Length != 3)
    throw new ArgumentException("Usage: ProcessControlTests <native DLL> <x64 DemoTarget> <x86 DemoTarget>");

string library = Path.GetFullPath(args[0]);
NativeLibrary.SetDllImportResolver(typeof(NativeEngine).Assembly,
    (name, _, _) => name == "memory_core.dll" ? NativeLibrary.Load(library) : 0);
int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    Console.WriteLine("PASS " + label);
    ++checks;
}
async Task<bool> Changed(Func<int> read, int initial)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(3))
    {
        await Task.Delay(20);
        if (read() != initial) return true;
    }
    return false;
}

foreach (string fixture in args.Skip(1))
{
    string path = Path.GetFullPath(fixture);
    using var target = Process.Start(new ProcessStartInfo(path, "--trace-test")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
    }) ?? throw new InvalidOperationException("Cannot start owned fixture " + path);
    try
    {
        string line = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException("Fixture did not report its address.");
        await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        ulong address = Convert.ToUInt64(line.Split(' ')[2], 16);
        using var engine = new NativeEngine(target.Id);
        using var pause = new ProcessControlService(target.Id);
        Check(engine.ProcessId == target.Id && Marshal.SizeOf<ScanRequest>() == 56 && Marshal.SizeOf<ScanHistoryInfo>() == 48,
            "managed metadata and ABI " + Path.GetFileName(path));
        int Read() => BitConverter.ToInt32(engine.Read(address, 4));
        int before = Read();
        Check(await Changed(Read, before), "target runs before pause " + Path.GetFileName(path));
        pause.Pause();
        Check(pause.IsPaused, "pause reports paused " + Path.GetFileName(path));
        before = Read();
        await Task.Delay(200);
        Check(Read() == before, "target computation paused " + Path.GetFileName(path));
        pause.Pause();
        pause.Resume();
        Check(!pause.IsPaused, "idempotent pause owns one increment " + Path.GetFileName(path));
        before = Read();
        Check(await Changed(Read, before), "target resumes " + Path.GetFileName(path));

        target.Refresh();
        uint threadId = (uint)target.Threads[0].Id;
        nint external = Probe.OpenThread(0x100002, false, threadId);
        if (external == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Check(Probe.SuspendThread(external) == 0, "establish external suspend increment");
            pause.Pause();
            pause.Resume();
            Check(Probe.SuspendThread(external) == 1, "owned pause preserves external increment");
            Check(Probe.ResumeThread(external) == 2, "remove probe increment");
            Check(Probe.ResumeThread(external) == 1, "remove external increment");
        }
        finally { Probe.CloseHandle(external); }

        pause.Pause();
        pause.Dispose();
        Check(!pause.IsPaused, "Dispose resumes owned increments");
        before = Read();
        Check(await Changed(Read, before), "target runs after Dispose");
        var helperStart = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        // The runner executes this assembly with its fixed portable dotnet host.
        helperStart.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        helperStart.ArgumentList.Add("--pause-helper");
        helperStart.ArgumentList.Add(target.Id.ToString());
        using (var helper = Process.Start(helperStart) ?? throw new InvalidOperationException("Cannot start cleanup helper."))
        {
            Check(await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "PAUSED", "helper pauses owned target");
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(helper.ExitCode == 0, "helper exits normally");
        }
        before = Read();
        Check(await Changed(Read, before), "normal controller exit automatically resumes target");
        using var exitPause = new ProcessControlService(target.Id);
        exitPause.Pause();
        target.Kill();
        await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(150);
        Check(!exitPause.IsPaused, "target exit clears suspended handles");
    }
    finally
    {
        if (!target.HasExited) target.Kill();
        await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
}
Console.WriteLine($"{checks} process control checks passed");

static class Probe
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint SuspendThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint thread);
}
