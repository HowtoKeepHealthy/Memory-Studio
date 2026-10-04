using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace MemoryStudio;

public sealed partial class MainViewModel
{
    private bool _hexDisplayEnabled, _floatToleranceEnabled = true;
    private string _floatToleranceText = "0.001", _relativeToleranceText = "0.000001", _searchUpperValue = "200";
    private ProcessControlService? _processControl;
    private MemoryEditHistory? _editHistory;
    private ResultRow[] _visibleResults = [];
    private readonly List<(Window Window, ulong FirstPage, ulong LastPage)> _traces = [];
    private readonly List<ScanUiState> _scanUiHistory = [];
    private readonly List<(DateTime At, string Description, RecordState[] States)> _recordHistory = [];
    private readonly HashSet<WatchRow> _subscribedWatches = [];
    private sealed record ScanUiState(int Type, int Size, bool HasScan, int Mode, string Value, string Upper, ulong Page,
        Dictionary<ulong, (ulong Address, int Type, int Size)> Overrides);
    private sealed record RecordState(object Row, ulong Address, int Type, int Size, byte[] Bytes, string Description,
        string Expression, int[] Offsets, bool Frozen);
    public ICommand AddManualAddressCommand { get; private set; } = null!;
    public ICommand UndoEditCommand { get; private set; } = null!;
    public ICommand UndoScanCommand { get; private set; } = null!;
    public ICommand TogglePauseCommand { get; private set; } = null!;
    public ICommand OpenPointerScannerCommand { get; private set; } = null!;
    public ICommand OpenStructureViewerCommand { get; private set; } = null!;
    public ICommand ImportCeTableCommand { get; private set; } = null!;
    public ICommand ExportCeTableCommand { get; private set; } = null!;
    public bool IsPaused => _processControl?.IsPaused == true;
    public string PauseButtonText => IsPaused ? "恢复进程" : "暂停进程";
    public bool FloatToleranceEnabled { get => _floatToleranceEnabled; set => Set(ref _floatToleranceEnabled, value); }
    public string FloatToleranceText { get => _floatToleranceText; set => Set(ref _floatToleranceText, value); }
    public string RelativeToleranceText { get => _relativeToleranceText; set => Set(ref _relativeToleranceText, value); }
    public string SearchUpperValue { get => _searchUpperValue; set => Set(ref _searchUpperValue, value); }
    public bool HexDisplayEnabled
    {
        get => _hexDisplayEnabled;
        set
        {
            if (value == _hexDisplayEnabled) return;
            try
            {
                string ConvertInput(int type, string input) => string.IsNullOrWhiteSpace(input) || type > 5 ? input :
                    ValueCodec.Format(type, ValueCodec.Parse(type, input, _hexDisplayEnabled), value);
                string search = ConvertInput(SelectedType.Value, SearchValue);
                string upper = ConvertInput(SelectedType.Value, SearchUpperValue);
                string edit = SelectedWatch == null ? EditValueText : ConvertInput(SelectedWatch.Type, EditValueText);
                Set(ref _hexDisplayEnabled, value); SearchValue = search; SearchUpperValue = upper; EditValueText = edit;
                foreach (var row in Results) { row.Hexadecimal = value; row.Apply(row.Address, row.Type, row.ByteSize, row.RawValue); }
                foreach (var row in Watches)
                {
                    row.Hexadecimal = value;
                    if (row.ValueText != "不可读取") row.ValueText = ValueCodec.Format(row.Type, row.FrozenValue, value);
                }
                StatusText = value ? "已切换十六进制；浮点数以 IEEE 754 位模式显示。" : "已切换十进制数值。";
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            { StatusText = "请先修正输入后切换进制：" + ex.Message; Notify(); }
        }
    }
    private void InitializeExtendedCommands()
    {
        Watches.CollectionChanged += (_, _) => SyncWatchRegistrations();
        AddManualAddressCommand = Command(AddManualAddress, () => IsAttached && !IsBusy);
        UndoEditCommand = Async(UndoEditAsync, () => IsAttached && !IsBusy && (_editHistory?.CanUndo == true || _recordHistory.Count > 0));
        UndoScanCommand = Async(UndoScanAsync, () => IsAttached && !IsBusy && _engine?.History.CanUndo == true);
        TogglePauseCommand = Async(TogglePauseAsync, () => IsAttached && !IsBusy && _attachedProcessId != Environment.ProcessId && !_traces.Any(t => t.Window is AccessTraceWindow trace && trace.HasProtectedPages));
        OpenPointerScannerCommand = Command(() =>
        {
            if (_engine == null) return;
            int pid = _attachedProcessId;
            var window = new PointerScannerWindow(pid, SelectedWatch?.Address ?? SelectedResult?.Address ?? 0, (expression, offsets, address) =>
            {
                if (pid != _attachedProcessId) { StatusText = "目标进程已切换，请重新连接指针扫描所属进程再添加。"; return; }
                AddPointerWatch(expression, offsets, address);
            }) { Owner = Application.Current.MainWindow };
            window.Show();
        }, () => IsAttached && !IsBusy);
        OpenStructureViewerCommand = Command(() =>
        {
            if (_engine == null) return;
            var reader = new NativeEngine(_attachedProcessId);
            try { new StructureViewerWindow(reader, SelectedWatch?.Address ?? SelectedResult?.Address ?? 0, true) { Owner = Application.Current.MainWindow }.Show(); }
            catch { reader.Dispose(); throw; }
        }, () => IsAttached && !IsBusy);
        ImportCeTableCommand = Command(ImportCeTable, () => IsAttached && !IsBusy);
        ExportCeTableCommand = Command(ExportCeTable, () => !IsBusy && Watches.Count > 0);
    }
    private void ShowHexViewer(ulong address)
    {
        var reader = new NativeEngine(_attachedProcessId);
        try { new HexViewerWindow(reader, address, _attachedProcessId, true) { Owner = Application.Current.MainWindow }.Show(); }
        catch { reader.Dispose(); throw; }
    }
    private void ShowDisassembly(ulong? address)
    {
        var reader = new NativeEngine(_attachedProcessId);
        try { new DisassemblyWindow(reader, _attachedProcessId, address, ownsEngine: true) { Owner = Application.Current.MainWindow }.Show(); }
        catch { reader.Dispose(); throw; }
    }
    private void SyncWatchRegistrations()
    {
        var current = Watches.ToHashSet();
        foreach (var row in _subscribedWatches.Where(w => !current.Contains(w)).ToArray())
        { row.PropertyChanged -= WatchFreezeChanged; _editHistory?.UntrackWatch(row); _subscribedWatches.Remove(row); }
        foreach (var row in current.Where(w => !_subscribedWatches.Contains(w)))
        { row.PropertyChanged += WatchFreezeChanged; _subscribedWatches.Add(row); _editHistory?.TrackWatch(row); }
    }
    private void WatchFreezeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is WatchRow row && e.PropertyName is nameof(WatchRow.IsFrozen) or nameof(WatchRow.Address)) _editHistory?.TrackWatch(row);
    }
    public void SetVisibleResults(IEnumerable<ResultRow> rows) => _visibleResults = rows.Distinct().Take(1000).ToArray();
    private bool IsTracedAddress(ulong address, int size)
    {
        ulong end = address > ulong.MaxValue - (ulong)Math.Max(1, size) + 1 ? ulong.MaxValue : address + (ulong)Math.Max(1, size) - 1;
        return _traces.Any(t => t.Window is AccessTraceWindow trace && trace.HasProtectedPages && address <= t.LastPage && end >= t.FirstPage);
    }
    public async Task<bool> CloseTraceWindowsAsync()
    {
        foreach (var entry in _traces.ToArray())
            if (entry.Window is AccessTraceWindow trace && !await trace.CloseSafelyAsync())
            { StatusText = "来源追踪尚未安全脱离；请在追踪窗口停止后再关闭程序。"; return false; }
        try { if (_processControl?.IsPaused == true) await Task.Run(_processControl.Resume); }
        catch (Exception ex) { StatusText = "退出前恢复目标进程失败，请重试恢复：" + ex.Message; return false; }
        return true;
    }
    private string FormatValue(int type, byte[] bytes) => ValueCodec.Format(type, bytes, HexDisplayEnabled);
    private void InitializeProcessFeatures(int pid)
    {
        _processControl?.Dispose(); _processControl = pid == Environment.ProcessId ? null : new ProcessControlService(pid);
        _editHistory = MemoryEditHistory.ForProcess(pid); _recordHistory.Clear(); _scanUiHistory.Clear(); _visibleResults = [];
        SyncWatchRegistrations();
        if (EndAddress is "" or "0x100000000" or "0x0000800000000000")
            try { EndAddress = ProcessInspector.DetectArchitecture(pid).Bitness == 32 ? "0x100000000" : "0x0000800000000000"; } catch { EndAddress = "0x0000800000000000"; }
        Notify(nameof(IsPaused)); Notify(nameof(PauseButtonText)); RefreshCommands();
    }
    private ScanUiState CaptureScanUi() => new(_scanType, _scanSize, _hasScan, SelectedScanMode.Value, SearchValue, SearchUpperValue, _page,
        _resultOverrides.ToDictionary(p => p.Key, p => p.Value));
    private void RememberScanUi(ScanUiState state)
    {
        _scanUiHistory.Add(state);
        int count = (int)(_engine?.History.UndoCount ?? 0);
        while (_scanUiHistory.Count > count) _scanUiHistory.RemoveAt(0);
    }
    private async Task UndoScanAsync()
    {
        var engine = _engine!; IsBusy = true;
        try
        {
            if (_watchJob != null) await _watchJob;
            int status = await Task.Run(engine.UndoScan);
            if (status != 0) throw new InvalidOperationException(engine.LastScanError);
            var history = engine.History;
            ScanUiState? state = _scanUiHistory.LastOrDefault();
            if (state != null) _scanUiHistory.RemoveAt(_scanUiHistory.Count - 1);
            _hasScan = history.HasScan; _scanType = (int)history.Type; _scanSize = (int)history.ByteWidth;
            _total = engine.Count; _page = state?.Page ?? 0; _showingAllResults = false; _resultOverrides.Clear();
            if (_page * PageSize >= _total) _page = 0;
            if (state != null)
            {
                SearchValue = state.Value; SearchUpperValue = state.Upper; SelectedScanMode = ScanModes.Single(m => m.Value == state.Mode);
                foreach (var pair in state.Overrides) _resultOverrides[pair.Key] = pair.Value;
            }
            if (_hasScan) { SelectedType = TypeOptions.Single(t => t.Value == _scanType); await LoadPageAsync(); }
            else { Results.Clear(); SelectedResult = null; }
            Notify(nameof(ResultSummary)); Notify(nameof(PageLabel)); ScanProgress = 0;
            StatusText = $"已撤销扫描，恢复 {_total:N0} 个候选地址及其比较基准。";
        }
        finally { IsBusy = false; }
    }
    private static RecordState CaptureRecord(object row) => row switch
    {
        ResultRow r => new(r, r.Address, r.Type, r.ByteSize, r.RawValue.ToArray(), "", "", [], false),
        WatchRow w => new(w, w.Address, w.Type, w.Size, w.FrozenValue.ToArray(), w.Description, w.AddressExpression, w.PointerOffsets.ToArray(), w.IsFrozen),
        _ => throw new ArgumentException("未知记录。")
    };
    private void RememberRecords(string description, IEnumerable<RecordState> states)
    {
        var snapshot = states.ToArray();
        if (snapshot.Length == 0) return;
        _recordHistory.Add((DateTime.UtcNow, description, snapshot));
        // Keep a bounded metadata history alongside the bounded native memory history.
        long bytes = _recordHistory.Sum(entry => entry.States.Sum(s => 128L + s.Bytes.Length + s.Description.Length * 2L));
        while (_recordHistory.Count > 1 && (_recordHistory.Count > 100 || bytes > 32L * 1024 * 1024))
        {
            bytes -= _recordHistory[0].States.Sum(s => 128L + s.Bytes.Length + s.Description.Length * 2L); _recordHistory.RemoveAt(0);
        }
        RefreshCommands();
    }
    public async Task UndoEditAsync()
    {
        if (_engine == null || IsBusy) return;
        IsBusy = true;
        try
        {
            if (_watchJob != null) await _watchJob;
            if (_recordHistory.Count > 0 && (_editHistory?.CanUndo != true || _recordHistory[^1].At >= _editHistory.LatestTimestamp))
            {
                var entry = _recordHistory[^1]; _recordHistory.RemoveAt(_recordHistory.Count - 1);
                foreach (var state in entry.States)
                {
                    if (state.Row is ResultRow result)
                    {
                        result.Apply(state.Address, state.Type, state.Size, state.Bytes);
                        _resultOverrides[result.SourceAddress] = (state.Address, state.Type, state.Size);
                    }
                    else if (state.Row is WatchRow watch)
                    {
                        watch.IsFrozen = false; watch.Address = state.Address; watch.Type = state.Type; watch.Size = state.Size;
                        watch.FrozenValue = state.Bytes; watch.Description = state.Description; watch.AddressExpression = state.Expression;
                        watch.PointerOffsets = state.Offsets; watch.ValueText = FormatValue(state.Type, state.Bytes); watch.IsFrozen = state.Frozen;
                    }
                }
                StatusText = "已撤销：" + entry.Description;
            }
            else if (_editHistory?.CanUndo == true)
            {
                var undo = await Task.Run(() => _editHistory.Undo(_engine));
                foreach (var change in undo.Changes)
                    foreach (var watch in Watches.Where(w => w.Address < change.Address + (ulong)change.Before.Length && change.Address < w.Address + (ulong)w.Size))
                    {
                        try { watch.FrozenValue = _engine.Read(watch.Address, watch.Size); watch.ValueText = FormatValue(watch.Type, watch.FrozenValue); }
                        catch { watch.IsFrozen = false; }
                    }
                StatusText = $"撤销 {undo.Description}：恢复 {undo.Restored:N0}/{undo.Attempted:N0} 处。" + (undo.Errors.Count > 0 ? string.Join("；", undo.Errors.Take(3)) : "");
            }
        }
        finally { IsBusy = false; }
        await RefreshLiveValuesAsync();
    }
    public Task WriteRecordsValueAsync(IReadOnlyList<object> rows, string value)
    {
        if (rows.Count == 0) return Task.CompletedTask;
        var data = RecordData(rows[0]);
        return ApplyRecordsEditAsync(rows, RecordEditKind.Value, new RecordEditResult(value, data.Type, data.Size, HexDisplayEnabled));
    }
    private async Task TogglePauseAsync()
    {
        if (_processControl == null) return;
        if (_traces.Any(t => t.Window is AccessTraceWindow trace && trace.HasProtectedPages)) throw new InvalidOperationException("请先停止来源追踪，再暂停目标进程。");
        IsBusy = true;
        try
        {
            if (_watchJob != null) await _watchJob;
            await Task.Run(() => { if (_processControl.IsPaused) _processControl.Resume(); else _processControl.Pause(); });
            Notify(nameof(IsPaused)); Notify(nameof(PauseButtonText));
            StatusText = IsPaused ? "目标进程已暂停；可查看和修改内存，关闭程序或切换进程会恢复。" : "目标进程已恢复。";
        }
        finally { IsBusy = false; }
    }
    private void AddManualAddress()
    {
        if (_engine == null) return;
        var dialog = new ManualAddressWindow(_engine, _attachedProcessId, HexDisplayEnabled) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        Watches.Add(dialog.Result); SelectedWatch = dialog.Result; RefreshCommands(); StatusText = "地址已手动加入监视表。";
    }
    private void AddPointerWatch(string expression, int[] offsets, ulong address)
    {
        if (_engine == null) return;
        try
        {
            byte[] bytes = _engine.Read(address, 4);
            var row = new WatchRow { Address = address, AddressExpression = expression, PointerOffsets = offsets, Type = 2, Size = 4,
                FrozenValue = bytes, ValueText = FormatValue(2, bytes), Hexadecimal = HexDisplayEnabled, Description = "指针地址" };
            Watches.Add(row); SelectedWatch = row; RefreshCommands(); StatusText = "指针链已加入地址表，将在每次刷新时重新解析。";
        }
        catch (Exception ex) { StatusText = FriendlyError(ex); }
    }
    private void ImportCeTable()
    {
        var dialog = new OpenFileDialog { Filter = "Cheat Engine 数据表|*.CT" };
        if (dialog.ShowDialog() != true) return;
        var entries = CeTableCodec.Load(dialog.FileName);
        var resolver = new AddressResolver(_engine!, _attachedProcessId);
        var additions = new List<WatchRow>(); int unreadable = 0;
        foreach (var entry in entries.Entries)
        {
            ulong address = 0; byte[] bytes = [];
            try { address = resolver.Resolve(entry.Expression ?? entry.Address, entry.Offsets); bytes = _engine!.Read(address, entry.Size); } catch { unreadable++; }
            additions.Add(new WatchRow { Address = address, Type = entry.Type, Size = entry.Size, Description = entry.Description,
                AddressExpression = entry.Expression ?? entry.Address, PointerOffsets = entry.Offsets ?? [], FrozenValue = bytes,
                ValueText = bytes.Length > 0 ? FormatValue(entry.Type, bytes) : "不可读取", Hexadecimal = HexDisplayEnabled });
        }
        Watches.ReplaceAll(Watches.Concat(additions).ToArray()); RefreshCommands();
        StatusText = $"已导入 {additions.Count:N0} 条地址，当前不可解析 {unreadable:N0} 条；跳过 {entries.Skipped:N0} 条脚本或不支持的记录。";
    }
    private void ExportCeTable()
    {
        var dialog = new SaveFileDialog { Filter = "Cheat Engine 数据表|*.CT", FileName = "地址表.CT" };
        if (dialog.ShowDialog() != true) return;
        CeTableCodec.Save(dialog.FileName, Watches); StatusText = "地址和指针数据已导出为 .CT 表。";
    }
}
