using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace MemoryStudio;

public static class ElevationService
{
    public static bool TryRelaunch(string[] arguments)
    {
        if (arguments.Contains("--no-elevate") || arguments.Any(a => a is "--self-test" or "--screenshot") || IsAdministrator()) return false;
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !System.IO.Path.GetFileName(executable).Equals("MemoryStudio.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            // Marker prevents a loop if Windows starts a limited token for unusual policies.
            foreach (string argument in arguments.Append("--no-elevate")) start.ArgumentList.Add(argument);
            using var child = Process.Start(start); return child != null;
        }
        catch (Win32Exception) { return false; } // UAC cancellation keeps the ordinary UI usable.
        catch (InvalidOperationException) { return false; }
    }
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
