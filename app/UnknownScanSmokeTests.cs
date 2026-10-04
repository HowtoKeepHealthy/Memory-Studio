using System.Runtime.InteropServices;

namespace MemoryStudio;

internal static class UnknownScanSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const int bytes = 12 * 1024 * 1024;
        nint memory = Marshal.AllocHGlobal(bytes);
        MainViewModel? vm = null;
        try
        {
            Marshal.Copy(new byte[bytes], 0, memory, bytes);
            vm = new MainViewModel { StartAddress = $"0x{memory:X}", EndAddress = $"0x{(ulong)memory + bytes - 1:X}", SearchValue = "" };
            await vm.AttachToProcessAsync(Environment.ProcessId);
            vm.SelectedScanMode = vm.ScanModes.Single(m => m.Value == 1);
            check(vm.FirstScanCommand.CanExecute(null), "Unknown initial scan accepts an empty search value");
            await vm.ScanAsync(false);
            check(vm.Results.Count == 200 && vm.ResultSummary.Contains("3,145,728") && vm.ResultSummary.Contains("快照"), "Unknown ViewModel scan stores over two million candidates with a bounded page");
            check(vm.SelectedScanMode.Value == 2 && vm.NextScanCommand.CanExecute(null), "Unknown snapshot enables changed-value filtering");
            check(!await vm.SelectAllScanResultsAsync() && vm.Results.Count == 200 && vm.StatusText.Contains("继续筛选"), "Large unknown snapshot does not allocate millions of UI rows for Select All");
            Marshal.WriteInt32(memory, 16, 1234567); Marshal.WriteInt32(memory, bytes - 4, 7654321);
            await vm.ScanAsync(true);
            check(vm.Results.Count == 2 && vm.Results[0].Address == (ulong)memory + 16 && vm.Results[1].Address == (ulong)memory + bytes - 4, "Changed filtering finds both early and final unknown candidates");
            vm.SearchValue = "7654321"; vm.SelectedScanMode = vm.ScanModes[0]; await vm.ScanAsync(true);
            check(vm.Results.Count == 1 && vm.Results[0].Address == (ulong)memory + bytes - 4, "Unknown pipeline narrows to an exact value");
            vm.UndoScanCommand.Execute(null); await WaitUntil(() => !vm.IsBusy);
            check(vm.Results.Count == 2, "Undo restores the last compact candidate set");
            vm.UndoScanCommand.Execute(null); await WaitUntil(() => !vm.IsBusy);
            check(vm.ResultSummary.Contains("3,145,728") && vm.Results.Count == 200, "Undo restores the full unknown snapshot without expanding UI rows");
            vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 4);
            vm.StartAddress = $"0x{(ulong)memory + 64:X}"; vm.EndAddress = $"0x{(ulong)memory + 67:X}";
            vm.SearchValue = ""; vm.FloatToleranceText = ""; vm.RelativeToleranceText = "";
            vm.SelectedScanMode = vm.ScanModes.Single(m => m.Value == 1); await vm.ScanAsync(false);
            check(vm.Results.Count == 1, "Float unknown snapshot does not require comparison input or tolerance");
            vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 6);
            vm.SelectedScanMode = vm.ScanModes.Single(m => m.Value == 1);
            check(!vm.FirstScanCommand.CanExecute(null), "Unknown initial scan is disabled for text types");
            vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 0); vm.SearchValue = "173";
            vm.SelectedScanMode = vm.ScanModes[0]; Marshal.WriteByte(memory, 127, 173);
            vm.StartAddress = vm.EndAddress = $"0x{(ulong)memory + 127:X}";
            await vm.ScanAsync(false);
            check(vm.Results.Count == 1 && vm.Results[0].Address == (ulong)memory + 127, "Equal start and inclusive end scan exactly one byte");
            Marshal.WriteByte(memory, 128, 173);
            vm.StartAddress = $"0x{(ulong)memory + 120:X}"; vm.EndAddress = $"0x{(ulong)memory + 127:X}";
            await vm.ScanAsync(false);
            check(vm.Results.Count == 1 && vm.Results[0].Address == (ulong)memory + 127, "Inclusive endpoint includes the last byte and excludes the following byte");
        }
        finally { vm?.Dispose(); Marshal.FreeHGlobal(memory); }
    }
    private static async Task WaitUntil(Func<bool> ready)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!ready()) { if (clock.ElapsedMilliseconds > 10000) throw new TimeoutException("Unknown scan workflow timed out"); await Task.Delay(10); }
    }
}
