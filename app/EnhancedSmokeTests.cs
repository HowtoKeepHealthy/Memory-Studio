using System.IO;
using System.Runtime.InteropServices;

namespace MemoryStudio;

internal static class EnhancedSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        nint block = Marshal.AllocHGlobal(4096);
        MainViewModel? vm = null;
        string table = Path.Combine(Path.GetTempPath(), "MemoryStudio-Test-" + Guid.NewGuid().ToString("N") + ".CT");
        try
        {
            Marshal.Copy(new byte[4096], 0, block, 4096);
            Marshal.WriteInt32(block, 16, 400); Marshal.WriteInt32(block, 20, 450);
            vm = new MainViewModel { StartAddress = $"0x{block + 16:X}", EndAddress = $"0x{block + 24:X}" };
            await vm.AttachToProcessAsync(Environment.ProcessId);
            check(vm.EndAddress == $"0x{block + 24:X}", "Attach preserves a user-specified scan boundary");
            vm.SelectedScanMode = vm.ScanModes.Single(m => m.Value == 6); vm.SearchValue = "425";
            await vm.ScanAsync(false);
            check(vm.Results.Count == 1 && vm.Results[0].Address == (ulong)block + 20, "Greater-than first scan works through ViewModel");
            Marshal.WriteInt32(block, 20, 460);
            vm.SelectedScanMode = vm.ScanModes[0]; vm.SearchValue = "999"; await vm.ScanAsync(true);
            check(vm.Results.Count == 0 && vm.UndoScanCommand.CanExecute(null), "Empty successful rescan can be undone");
            vm.UndoScanCommand.Execute(null); await WaitUntil(() => !vm.IsBusy);
            check(vm.Results.Count == 1 && vm.Results[0].ValueText == "460", "Undo scan restores candidates and live values");
            vm.SelectedScanMode = vm.ScanModes.Single(m => m.Value == 4); await vm.ScanAsync(true);
            check(vm.Results.Count == 1, "Undo restores the previous numeric comparison snapshot");
            var result = vm.Results[0];
            Marshal.WriteInt32(block, 20, 470); await vm.RefreshLiveValuesAsync();
            check(result.ValueText == "470" && result.RawValue.SequenceEqual(BitConverter.GetBytes(470)), "Scan results refresh even without watch entries");
            vm.SelectedResult = result; vm.AddSelectedCommand.Execute(null); var first = vm.SelectedWatch!;
            var second = new WatchRow { Address = (ulong)block + 16, Type = 2, Size = 4, FrozenValue = BitConverter.GetBytes(400), ValueText = "400" };
            vm.Watches.Add(second);
            await vm.WriteRecordsValueAsync([first, second], "9999");
            check(Marshal.ReadInt32(block, 16) == 9999 && Marshal.ReadInt32(block, 20) == 9999, "Toolbar batch write changes every selected address");
            first.IsFrozen = true; await vm.UndoEditAsync();
            await Task.Delay(600);
            check(Marshal.ReadInt32(block, 16) == 400 && Marshal.ReadInt32(block, 20) == 470 && first.IsFrozen && first.FrozenValue.SequenceEqual(BitConverter.GetBytes(470)), "Batch undo restores each original value and updates freeze targets");
            first.IsFrozen = false;
            first.IsFrozen = true;
            using (var browserEngine = new NativeEngine(Environment.ProcessId))
            {
                var shared = MemoryEditHistory.ForProcess(Environment.ProcessId);
                shared.Write(browserEngine, first.Address, BitConverter.GetBytes(777), "浏览器写入冻结地址");
                await Task.Delay(650);
                check(Marshal.ReadInt32(block, 20) == 777 && first.IsFrozen && first.FrozenValue.SequenceEqual(BitConverter.GetBytes(777)), "Independent browser edits update the main window's freeze target");
                shared.Undo(browserEngine); await Task.Delay(650);
                check(Marshal.ReadInt32(block, 20) == 470 && first.IsFrozen && first.FrozenValue.SequenceEqual(BitConverter.GetBytes(470)), "Independent browser undo survives the main freeze timer");
            }
            first.IsFrozen = false;
            await vm.ApplyRecordsEditAsync([first, second], RecordEditKind.Type, new("", 0, 1));
            await vm.UndoEditAsync();
            check(first.Type == 2 && second.Type == 2 && first.Size == 4 && second.Size == 4, "Undo restores a batch of record types");
            vm.SearchValue = "470"; vm.SearchUpperValue = "600"; vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 2); vm.SelectedWatch = first;
            vm.EditValueText = "470"; vm.HexDisplayEnabled = true;
            check(vm.SearchValue == "0x000001D6" && vm.EditValueText == "0x000001D6" && first.ValueText == "0x000001D6", "Radix switching converts search, write and displayed values together");
            vm.HexDisplayEnabled = false;
            check(vm.SearchValue == "470" && vm.SearchUpperValue == "600", "Decimal conversion preserves existing values");
            Marshal.Copy(BitConverter.GetBytes(1.2345678f), 0, block + 64, 4);
            vm.StartAddress = $"0x{block + 64:X}"; vm.EndAddress = $"0x{block + 68:X}"; vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 4);
            vm.SearchValue = "1.234"; vm.SelectedScanMode = vm.ScanModes[0]; await vm.ScanAsync(false);
            check(vm.Results.Count == 1, "Float exact scan defaults to tolerance around the entered value");
            vm.FloatToleranceEnabled = false; await vm.ScanAsync(false); check(vm.Results.Count == 0, "Float tolerance can be disabled for exact bitwise-value comparisons");
            foreach (var pair in new[] { (0, "255"), (1, "-1"), (2, "-12345"), (3, "-9223372036854775808"), (4, "1.25"), (5, "-0.5") })
            {
                byte[] bytes = ValueCodec.Parse(pair.Item1, pair.Item2);
                check(ValueCodec.Parse(pair.Item1, ValueCodec.Format(pair.Item1, bytes, true), true).SequenceEqual(bytes), $"Hexadecimal bit patterns round-trip type {pair.Item1}");
            }
            var pattern = ValueCodec.ParsePattern("48 8B ?? A? ?F");
            check(pattern.Value.SequenceEqual(new byte[] { 0x48, 0x8B, 0, 0xA0, 0x0F }) && pattern.Mask.SequenceEqual(new byte[] { 255, 255, 0, 0xF0, 0x0F }), "AOB nibble and full-byte wildcard parsing");
            var pointer = new WatchRow { Address = (ulong)block, AddressExpression = "\"game.exe\"+1A0", PointerOffsets = [0x20, -4, 8], Type = 7, Size = 16, Description = "指针\"测试" };
            CeTableCodec.Save(table, [pointer]); var imported = CeTableCodec.Load(table);
            check(imported.Entries.Count == 1 && imported.Entries[0].Offsets!.SequenceEqual(pointer.PointerOffsets) && imported.Entries[0].Size == 16, "CT pointer ordering and Unicode byte lengths round-trip");
            check(!ElevationService.TryRelaunch(["--no-elevate"]) && !ElevationService.TryRelaunch(["--self-test"]), "Automated checks and explicit normal launch never trigger UAC");
        }
        finally { vm?.Dispose(); Marshal.FreeHGlobal(block); if (File.Exists(table)) File.Delete(table); }
    }
    private static async Task WaitUntil(Func<bool> predicate)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate()) { if (started.ElapsedMilliseconds > 10000) throw new TimeoutException("Enhanced workflow timed out"); await Task.Delay(10); }
    }
}
