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
            await EnhancedSmokeTests.RunAsync(Check);
            await UnknownScanSmokeTests.RunAsync(Check);
            block = Marshal.AllocHGlobal(4096);
            Marshal.Copy(new byte[4096], 0, block, 4096);
            Marshal.WriteInt32(block, 16, 987654321);
            vm = new MainViewModel { StartAddress = $"0x{block:X}", EndAddress = $"0x{(ulong)block + 4095:X}", SearchValue = "987654321" };
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
            var record = vm.Results[0];
            await vm.ApplyRecordEditAsync(record, RecordEditKind.Value, new RecordEditResult("12345", record.Type, record.ByteSize));
            Check(Marshal.ReadInt32(block, 16) == 12345 && record.ValueText == "12345", "Result value editor writes the selected record");
            await vm.ApplyRecordEditAsync(record, RecordEditKind.Type, new RecordEditResult("", 0, 1));
            Check(record.Type == 0 && record.ByteSize == 1 && record.ValueText == "57", "Result type editor reinterprets the same memory");
            vm.SelectedResult = record; vm.AddSelectedCommand.Execute(null);
            var editedWatch = vm.Watches.Single();
            Check(editedWatch.Type == 0 && editedWatch.Size == 1, "Adding an edited result preserves its type and width");
            Marshal.WriteByte(block, 64, 7);
            editedWatch.IsFrozen = true;
            await vm.ApplyRecordEditAsync(editedWatch, RecordEditKind.Address, new RecordEditResult($"0x{(ulong)block + 64:X}", 0, 1));
            Check(editedWatch.Address == (ulong)block + 64 && editedWatch.ValueText == "7" && !editedWatch.IsFrozen, "Address editor reads the new address and disables freezing");
            await vm.ApplyRecordEditAsync(editedWatch, RecordEditKind.Description, new RecordEditResult("生命值追踪", 0, 1));
            Check(editedWatch.Description == "生命值追踪", "Description editor updates the actual watch row");
            bool invalidEditRejected = false;
            try { await vm.ApplyRecordEditAsync(editedWatch, RecordEditKind.Type, new RecordEditResult("", 7, 3)); }
            catch (ArgumentException) { invalidEditRejected = true; }
            Check(invalidEditRejected && editedWatch.Type == 0, "Invalid UTF-16 width leaves the record unchanged");
            Marshal.WriteByte(block, 80, 9);
            var secondWatch = new WatchRow { Address = (ulong)block + 80, Type = 0, Size = 1, FrozenValue = [9], ValueText = "9", Description = "第二项" };
            vm.Watches.Add(secondWatch);
            await vm.ApplyRecordsEditAsync([editedWatch, secondWatch], RecordEditKind.Value, new RecordEditResult("42", 0, 1));
            Check(Marshal.ReadByte(block, 64) == 42 && Marshal.ReadByte(block, 80) == 42, "Batch value editing writes every selected record");
            bool invalidBatchRejected = false;
            try { await vm.ApplyRecordsEditAsync([record, editedWatch], RecordEditKind.Value, new RecordEditResult("999", 2, 4)); }
            catch (ArgumentException) { invalidBatchRejected = true; }
            Check(invalidBatchRejected && Marshal.ReadByte(block, 64) == 42, "Batch validation prevents all writes when one type cannot accept the value");
            await vm.HandleRecordsActionAsync([editedWatch, secondWatch], "freeze");
            Check(editedWatch.IsFrozen && secondWatch.IsFrozen, "Batch freeze includes every selected watch");
            await vm.HandleRecordsActionAsync([editedWatch, secondWatch], "unfreeze");
            Check(!editedWatch.IsFrozen && !secondWatch.IsFrozen, "Batch unfreeze includes every selected watch");
            await vm.HandleRecordsActionAsync([editedWatch, secondWatch], "remove-watch");
            Check(vm.Watches.Count == 0, "Batch removal acts on the selected watch set");
            vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 2);
            vm.SelectedScanMode = vm.ScanModes[0]; vm.SearchValue = "0";
            await vm.ScanAsync(false);
            Check(vm.Results.Count == 200 && vm.NextPageCommand.CanExecute(null), "Large scan initially displays a bounded page");
            ulong editedCandidate = vm.Results[0].SourceAddress;
            await vm.ApplyRecordEditAsync(vm.Results[0], RecordEditKind.Type, new RecordEditResult("", 0, 1));
            Check(await vm.SelectAllScanResultsAsync() && vm.Results.Count > 1000 && !vm.NextPageCommand.CanExecute(null), "Select all spans every scan result page");
            Check(vm.Results.Single(r => r.SourceAddress == editedCandidate).Type == 0, "Cross-page selection preserves per-record type changes");
            var allResults = vm.Results.Cast<object>().ToArray();
            await vm.HandleRecordsActionAsync(allResults, "add-watch");
            Check(vm.Watches.Count == allResults.Length, "Cross-page batch addition includes every scan result");
            await vm.HandleRecordsActionAsync(allResults, "add-watch");
            Check(vm.Watches.Count == allResults.Length, "Repeated batch addition avoids duplicate watch entries");
            await vm.ApplyRecordsEditAsync(vm.Watches.Cast<object>().ToArray(), RecordEditKind.Value, new RecordEditResult("42", 2, 4));
            Check(vm.Watches.All(w => Marshal.ReadInt32((nint)w.Address) == 42), "All watch entries with mixed types can be changed in one batch");
            await vm.ApplyRecordsEditAsync(vm.Watches.Cast<object>().ToArray(), RecordEditKind.Type, new RecordEditResult("", 2, 4));
            Check(vm.Watches.All(w => w.Type == 2 && w.Size == 4 && w.ValueText == "42"), "Batch type changes reinterpret every selected watch consistently");
            await vm.HandleRecordsActionAsync(vm.Watches.Cast<object>().ToArray(), "remove-watch");
            Check(vm.Watches.Count == 0, "All watch entries can be removed in one batch");
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
