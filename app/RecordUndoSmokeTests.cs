using System.Runtime.InteropServices;

namespace MemoryStudio;

internal static class RecordUndoSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        nint block = VirtualAlloc(0, 8192, 0x3000, 4);
        if (block == 0) throw new InvalidOperationException("Cannot allocate record undo fixture.");
        using var vm = new MainViewModel { StartAddress = $"0x{block:X}", EndAddress = $"0x{block + 8191:X}" };
        try
        {
            await vm.AttachToProcessAsync(Environment.ProcessId);
            ulong start = (ulong)block;
            WatchRow Make(ulong address, int value, string description)
            {
                Marshal.WriteInt32((nint)address, value);
                return new() { Address = address, Type = 2, Size = 4, FrozenValue = BitConverter.GetBytes(value), ValueText = value.ToString(), Description = description };
            }
            var a = Make(start + 16, 10, "A"); var b = Make(start + 4096 + 20, 20, "B");
            vm.Watches.Add(a); vm.Watches.Add(b);
            int Read(WatchRow row) => Marshal.ReadInt32((nint)row.Address);
            Task Value(object row, int value) => vm.ApplyRecordEditAsync(row, RecordEditKind.Value, new(value.ToString(), 2, 4));
            Task Description(WatchRow row, string value) => vm.ApplyRecordEditAsync(row, RecordEditKind.Description, new(value, row.Type, row.Size));

            await vm.WriteRecordsValueAsync([a, b], "777");
            await vm.UndoRecordsAsync([a]);
            check(Read(a) == 10 && Read(b) == 777 && vm.CanUndoRecord(b) && !vm.CanUndoRecord(a), "Per-record undo removes only A from a shared value batch");
            await vm.UndoRecordsAsync([b]); check(Read(b) == 20, "B remains independently undoable after A");
            await Value(a, 11); await Value(b, 21); await Value(a, 12); await Value(b, 22);
            await vm.UndoRecordsAsync([a, b]);
            check(Read(a) == 11 && Read(b) == 21, "Selecting A+B rolls each record back exactly once despite interleaved edits");
            await vm.UndoRecordsAsync([a]); check(Read(a) == 10 && Read(b) == 21, "Repeated A undo leaves B's earlier step intact");
            await vm.UndoRecordsAsync([b]); check(Read(b) == 20, "Repeated B undo reaches its original value");

            await Description(a, "A1"); await Description(b, "B1"); await Value(a, 15);
            await vm.ApplyRecordEditAsync(a, RecordEditKind.Type, new("", 0, 1));
            await vm.UndoRecordsAsync([a]);
            check(a.Type == 2 && a.Size == 4 && Read(a) == 15 && a.Description == "A1", "Record undo chooses newer type metadata before an earlier value write");
            await vm.UndoRecordsAsync([a]); check(Read(a) == 10 && a.Description == "A1", "Next record undo restores its value without consuming description history");
            await vm.UndoRecordsAsync([a]); check(a.Description == "A" && b.Description == "B1", "Description history belongs to its own record");
            await vm.UndoRecordsAsync([b]); check(b.Description == "B", "Other description undo is still available");
            Marshal.WriteInt32(block + 64, 50);
            await vm.ApplyRecordEditAsync(a, RecordEditKind.Address, new($"0x{start + 64:X}", 2, 4));
            await Description(a, "moved"); await Value(b, 25);
            await vm.UndoRecordsAsync([a]); check(a.Address == start + 64 && a.Description == "A", "Description undo does not consume earlier address edit");
            await vm.UndoRecordsAsync([a]); check(a.Address == start + 16 && a.Type == 2 && Read(a) == 10 && Read(b) == 25, "Address undo restores the same record while preserving other records' writes");
            await vm.UndoRecordsAsync([b]);

            var alias = new WatchRow { Address = a.Address, Type = 2, Size = 4, FrozenValue = BitConverter.GetBytes(10), ValueText = "10", Description = "alias" };
            vm.Watches.Add(alias); a.IsFrozen = true;
            await vm.ApplyRecordEditAsync(a, RecordEditKind.Type, new("", 0, 1));
            await Value(alias, 99); await vm.UndoRecordsAsync([a]); await Task.Delay(650);
            check(a.Type == 2 && a.IsFrozen && Read(a) == 99 && a.FrozenValue.SequenceEqual(BitConverter.GetBytes(99)), "Metadata undo restores freezing against current bytes rather than overwriting a later alias write");
            await vm.UndoRecordsAsync([alias]); await Task.Delay(650);
            check(Read(a) == 10 && a.FrozenValue.SequenceEqual(BitConverter.GetBytes(10)), "Alias value undo synchronizes the restored frozen baseline");
            a.IsFrozen = false;
            await Value(a, 11); await Value(alias, 22); await vm.UndoRecordsAsync([a]);
            check(Read(a) == 22 && vm.CanUndoRecord(a) && vm.StatusText.Contains("重叠"), "Earlier record undo cannot overwrite a later unselected overlapping edit");
            await vm.UndoRecordsAsync([alias]); await vm.UndoRecordsAsync([a]);
            check(Read(a) == 10, "Undoing the later alias unlocks the preserved earlier record step");
            await Value(a, 33); await Value(alias, 44); await vm.UndoRecordsAsync([a, alias]);
            check(Read(a) == 10 && !vm.CanUndoRecord(a) && !vm.CanUndoRecord(alias), "Selected overlapping records undo in dependency order");
            using (var browser = new NativeEngine(Environment.ProcessId))
            {
                var history = MemoryEditHistory.ForProcess(Environment.ProcessId);
                await Value(a, 11); history.Write(browser, a.Address, BitConverter.GetBytes(22), "code patch", code: true);
                await vm.UndoRecordsAsync([a]); check(Read(a) == 22 && vm.CanUndoRecord(a), "A later browser code patch protects its bytes from record undo");
                history.Undo(browser); await vm.UndoRecordsAsync([a]);
                check(Read(a) == 10, "Browser global patch undo remains compatible with per-record undo");
            }
            await Value(a, 11); Marshal.WriteInt32((nint)a.Address, 55); await vm.UndoRecordsAsync([a]);
            check(Read(a) == 10 && !vm.CanUndoRecord(a), "Record undo restores its previous edit value even when the live target changed later");

            await Value(a, 11); await Value(b, 21);
            if (!VirtualProtect(block, 4096, 2, out _)) throw new InvalidOperationException("Protect undo fixture failed.");
            try
            {
                await vm.UndoRecordsAsync([a, b]);
                check(Read(a) == 11 && Read(b) == 20 && vm.CanUndoRecord(a) && !vm.CanUndoRecord(b), "Failed record undo remains retryable while an independent selected record succeeds");
            }
            finally { VirtualProtect(block, 4096, 4, out _); }
            await vm.UndoRecordsAsync([a]); check(Read(a) == 10, "Restoring page access permits the preserved record undo retry");
            Marshal.WriteInt64(block + 4092, 10);
            var edge = new WatchRow { Address = start + 4092, Type = 2, Size = 4, FrozenValue = BitConverter.GetBytes(10), ValueText = "10" };
            var wide = new WatchRow { Address = start + 4092, Type = 3, Size = 8, FrozenValue = BitConverter.GetBytes(10L), ValueText = "10" };
            vm.Watches.Add(edge); vm.Watches.Add(wide);
            await Value(edge, 20); await Value(wide, 40);
            if (!VirtualProtect(block + 4096, 4096, 2, out _)) throw new InvalidOperationException("Protect overlapping undo fixture failed.");
            try
            {
                await vm.UndoRecordsAsync([edge, wide]);
                check(Read(edge) == 40 && vm.CanUndoRecord(edge) && vm.CanUndoRecord(wide), "An unwritable later selected overlap also preserves its earlier dependent record step");
            }
            finally { VirtualProtect(block + 4096, 4096, 4, out _); }
            await vm.UndoRecordsAsync([edge, wide]);
            check(Read(edge) == 10 && !vm.CanUndoRecord(edge) && !vm.CanUndoRecord(wide), "Selected overlap retry restores both previous values in dependency order");
            await Value(a, 11); bool invalid = false;
            try { await vm.UndoRecordsAsync([a, new object()]); } catch (ArgumentException) { invalid = true; }
            check(invalid && Read(a) == 11, "Undo validates the entire selected record set before any writes");
            await vm.UndoRecordsAsync([a]);

            vm.SearchValue = "0"; vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 2); vm.SelectedScanMode = vm.ScanModes[0];
            await vm.ScanAsync(false); var original = vm.Results[0]; ulong source = original.SourceAddress;
            await Value(original, 13); await vm.ApplyRecordEditAsync(original, RecordEditKind.Type, new("", 0, 1));
            vm.NextPageCommand.Execute(null); await WaitUntil(() => !vm.IsBusy);
            vm.PreviousPageCommand.Execute(null); await WaitUntil(() => !vm.IsBusy);
            var fresh = vm.Results.Single(row => row.SourceAddress == source);
            check(!ReferenceEquals(original, fresh) && vm.CanUndoRecord(fresh), "Scan result history survives page reconstruction using its source address and scan scope");
            await vm.UndoRecordsAsync([fresh]); check(fresh.Type == 2 && fresh.ByteSize == 4 && Marshal.ReadInt32((nint)fresh.Address) == 13, "Recreated result undoes its latest type metadata");
            await vm.UndoRecordsAsync([fresh]); check(Marshal.ReadInt32((nint)fresh.Address) == 0, "Recreated result retains its preceding value undo");
            await vm.ApplyRecordEditAsync(fresh, RecordEditKind.Type, new("", 0, 1));
            await vm.SelectAllScanResultsAsync(); var allFresh = vm.Results.Single(row => row.SourceAddress == source);
            check(vm.CanUndoRecord(allFresh), "Select-all result replacement preserves per-record undo identity");
            await vm.UndoRecordsAsync([allFresh]); check(allFresh.Type == 2, "Select-all reconstructed result restores its own metadata");
            await Value(allFresh, 33); vm.SearchValue = "33"; await vm.ScanAsync(false);
            check(!vm.CanUndoRecord(vm.Results.Single()), "A new initial scan establishes a distinct result history scope");
            vm.UndoScanCommand.Execute(null); await WaitUntil(() => !vm.IsBusy);
            var recovered = vm.Results.Single(row => row.SourceAddress == source);
            check(vm.CanUndoRecord(recovered), "Scan undo restores its result history scope");
            await vm.UndoRecordsAsync([recovered]); check(Marshal.ReadInt32((nint)recovered.Address) == 0, "Restored scan scope can undo its prior record value");

            var notified = new HashSet<string>(); vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");
            vm.SelectedScanMode = vm.ScanModes.Single(t => t.Value == 1);
            check(!vm.SearchValueEnabled && !vm.SearchUpperEnabled && notified.Contains(nameof(vm.SearchValueEnabled)) && notified.Contains(nameof(vm.SearchUpperEnabled)), "Unknown initial mode disables both inputs and notifies bindings");
            vm.SelectedScanMode = vm.ScanModes.Single(t => t.Value == 8); check(vm.SearchValueEnabled && vm.SearchUpperEnabled, "Range mode enables lower and upper inputs");
            vm.SelectedScanMode = vm.ScanModes[0]; check(vm.SearchValueEnabled && !vm.SearchUpperEnabled, "Exact mode enables only its required input");
        }
        finally { vm.Dispose(); VirtualFree(block, 0, 0x8000); }
    }
    private static async Task WaitUntil(Func<bool> predicate)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate()) { if (started.ElapsedMilliseconds > 10000) throw new TimeoutException("Record undo workflow timed out."); await Task.Delay(10); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint bytes, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint bytes, uint protect, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint bytes, uint type);
}
