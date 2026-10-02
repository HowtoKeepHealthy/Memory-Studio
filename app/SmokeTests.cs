using System.IO;
using System.Runtime.InteropServices;

namespace MemoryStudio;

internal static class SmokeTests
{
    public static async Task<int> RunAsync(string reportPath)
    {
        int code = Run(reportPath);
        if (code != 0) return code;
        nint block = 0;
        MainViewModel? vm = null;
        var lines = new List<string>();
        try
        {
            await FeatureSmokeTests.RunAsync(Check);
            block = Marshal.AllocHGlobal(4096);
            Marshal.Copy(new byte[4096], 0, block, 4096);
            Marshal.WriteInt32(block, 16, 987654321);
            vm = new MainViewModel { StartAddress = $"0x{block:X}", EndAddress = $"0x{(ulong)block + 4096:X}", SearchValue = "987654321" };
            Check(!vm.FirstScanCommand.CanExecute(null), "First scan disabled before attach");
            vm.SelectedProcess = vm.Processes.Single(p => p.Id == Environment.ProcessId);
            vm.AttachCommand.Execute(null);
            await WaitUntil(() => vm.IsAttached && !vm.IsBusy);
            Check(vm.FirstScanCommand.CanExecute(null), "First scan enabled after attach");
            await vm.ScanAsync(false);
            Check(vm.Results.Count == 1 && vm.Results[0].Address == (ulong)block + 16, "ViewModel real exact scan and page");
            vm.SelectedResult = vm.Results[0];
            vm.AddSelectedCommand.Execute(null);
            Check(vm.Watches.Count == 1 && vm.SelectedWatch != null, "Result added to watch table");
            vm.EditValueText = "987654322";
            vm.WriteSelectedCommand.Execute(null);
            await WaitUntil(() => !vm.IsBusy && Marshal.ReadInt32(block, 16) == 987654322);
            Check(vm.Watches[0].ValueText == "987654322", "Watch value written through command");
            vm.Watches[0].IsFrozen = true;
            Marshal.WriteInt32(block, 16, 100);
            await WaitUntil(() => Marshal.ReadInt32(block, 16) == 987654322);
            Check(vm.Watches[0].IsFrozen, "Watch timer restores frozen value");
            vm.EditValueText = "987654323";
            vm.WriteSelectedCommand.Execute(null);
            await WaitUntil(() => !vm.IsBusy && Marshal.ReadInt32(block, 16) == 987654323);
            await Task.Delay(650);
            Check(Marshal.ReadInt32(block, 16) == 987654323 && vm.Watches[0].IsFrozen, "Writing a frozen watch updates the freeze value");
            vm.Watches[0].IsFrozen = false;
            vm.SelectedScanMode = vm.ScanModes.Single(m => m.Value == 4);
            await vm.ScanAsync(true);
            Check(vm.Results.Count == 1 && vm.Results[0].ValueText == "987654323", "ViewModel increased rescan");
            vm.RemoveWatchCommand.Execute(null);
            Check(vm.Watches.Count == 0, "Watch removed");
            lines.Add("PASS: all ViewModel workflow checks.");
            File.AppendAllLines(reportPath, lines); return 0;
        }
        catch (Exception ex) { lines.Add($"FAIL: Application integration: {ex}"); File.AppendAllLines(reportPath, lines); return 1; }
        finally { vm?.Dispose(); if (block != 0) Marshal.FreeHGlobal(block); }
        void Check(bool result, string label) { if (!result) throw new InvalidOperationException(label); lines.Add($"PASS: {label}"); }
        static async Task WaitUntil(Func<bool> predicate)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Workflow did not complete within 5 seconds"); await Task.Delay(10); }
        }
    }
    public static int Run(string reportPath)
    {
        var lines = new List<string>();
        nint block = 0;
        try
        {
            Check(Marshal.SizeOf<ScanRequest>() == 56, "C ABI scan request size");
            Check(Marshal.SizeOf<ScanProgressInfo>() == 40, "C ABI progress size");
            foreach (var type in ValueCodec.Types.Where(t => t.Value <= 5))
            {
                var bytes = ValueCodec.Parse(type.Value, "12");
                Check(ValueCodec.Format(type.Value, bytes) == "12", $"Value codec {type.Label}");
            }
            Check(ValueCodec.Parse(8, "DE AD BE EF").SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), "Byte pattern codec");
            Check(ValueCodec.Address("0xABCDEF") == 0xABCDEF, "Hex address codec");
            block = Marshal.AllocHGlobal(8192);
            Marshal.Copy(new byte[8192], 0, block, 8192);
            Marshal.WriteInt32(block, 16, 123456789);
            Marshal.WriteInt32(block, 64, 123456789);
            using var engine = new NativeEngine(Environment.ProcessId);
            var request = new ScanRequest { Type = 2, Mode = 0, StartAddress = (ulong)block, EndAddress = (ulong)block + 8192, Alignment = 4, MaxResults = 100, WritableOnly = 1 };
            Check(engine.Scan(request, BitConverter.GetBytes(123456789), false) == 0, "P/Invoke exact scan");
            Check(engine.Count == 2, "Native candidate count");
            var page = engine.Page(0, 1, 2, 4);
            Check(page.Count == 1 && page[0].Address == (ulong)block + 16 && page[0].ValueText == "123456789", "Paged current values");
            engine.Write((ulong)block + 16, BitConverter.GetBytes(123456790));
            Check(Marshal.ReadInt32(block, 16) == 123456790, "P/Invoke process write");
            request.Mode = 4;
            Check(engine.Scan(request, [], true) == 0 && engine.Count == 1, "Increased rescan");
            request.Mode = 3;
            Check(engine.Scan(request, [], true) == 0 && engine.Count == 1, "Updated snapshot unchanged rescan");
            Check(BitConverter.ToInt32(engine.Read((ulong)block + 16, 4)) == 123456790, "P/Invoke read");
            bool failed = false; try { engine.Read(1, 4); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "Invalid address reports an error");
            lines.Add("PASS: all managed/native integration checks.");
            Save(); return 0;
        }
        catch (Exception ex) { lines.Add($"FAIL: {ex}"); Save(); return 1; }
        finally { if (block != 0) Marshal.FreeHGlobal(block); }
        void Check(bool result, string label) { if (!result) throw new InvalidOperationException(label); lines.Add($"PASS: {label}"); }
        void Save() { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); File.WriteAllLines(reportPath, lines); }
    }
}
