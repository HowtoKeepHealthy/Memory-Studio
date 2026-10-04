using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MemoryStudio;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const uint PageSize = 200;
    private NativeEngine? _engine;
    private int _attachedProcessId;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Task? _watchJob;
    private CancellationTokenSource? _batchCancellation;
    private bool _watchUpdating, _disposed, _isBusy, _hasScan, _showingAllResults;
    private int _scanType = 2, _scanSize = 4;
    private ulong _page, _total;
    private ProcessItem? _selectedProcess;
    private ResultRow? _selectedResult;
    private WatchRow? _selectedWatch;
    private Option _selectedType = ValueCodec.Types[0];
    private Option _selectedScanMode;
    private string _searchValue = "100", _startAddress = "0x00000000", _endAddress = "0x0000800000000000", _editValueText = "";
    private string _processLabel = "尚未连接进程", _statusText = "选择进程并连接，或启动演示进程体验扫描。", _elapsedText = "等待扫描";
    private bool _alignmentEnabled = true, _writableOnly = true;
    private double _scanProgress;
    private readonly List<object> _commands = [];
    private readonly Dictionary<ulong, (ulong Address, int Type, int Size)> _resultOverrides = [];
    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public RecordCollection<ResultRow> Results { get; } = [];
    public RecordCollection<WatchRow> Watches { get; } = [];
    public IReadOnlyList<Option> TypeOptions => ValueCodec.Types;
    public IReadOnlyList<Option> ScanModes { get; } = [new("精确数值", 0), new("未知初始值", 1), new("数值发生变化", 2), new("数值保持不变", 3), new("数值增加", 4), new("数值减少", 5), new("大于", 6), new("小于", 7), new("数值在范围内", 8), new("增加了指定数值", 9), new("减少了指定数值", 10)];
    public ICommand RefreshProcessesCommand { get; }
    public ICommand AttachCommand { get; }
    public ICommand LaunchDemoCommand { get; }
    public ICommand FirstScanCommand { get; }
    public ICommand NextScanCommand { get; }
    public ICommand NewScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand AddSelectedCommand { get; }
    public ICommand OpenMemoryViewerCommand { get; }
    public ICommand OpenDisassemblyCommand { get; }
    public ICommand WriteSelectedCommand { get; }
    public ICommand RemoveWatchCommand { get; }
    public ICommand SaveTableCommand { get; }
    public ICommand LoadTableCommand { get; }

    public MainViewModel()
    {
        _selectedScanMode = ScanModes[0];
        RefreshProcessesCommand = Command(RefreshProcesses, () => !IsBusy);
        AttachCommand = Async(AttachAsync, () => SelectedProcess != null && !IsBusy);
        LaunchDemoCommand = Async(LaunchDemoAsync, () => !IsBusy);
        FirstScanCommand = Async(() => ScanAsync(false), () => IsAttached && !IsBusy && SelectedScanMode.Value is 0 or 1 or 6 or 7 or 8);
        NextScanCommand = Async(() => ScanAsync(true), () => IsAttached && _hasScan && !IsBusy && SelectedScanMode.Value != 1);
        NewScanCommand = Command(NewScan, () => !IsBusy);
        CancelScanCommand = Command(() => { _batchCancellation?.Cancel(); _engine?.Cancel(); StatusText = "正在停止当前操作…"; }, () => IsBusy);
        PreviousPageCommand = Async(() => ChangePage(-1), () => !IsBusy && !_showingAllResults && _page > 0);
        NextPageCommand = Async(() => ChangePage(1), () => !IsBusy && !_showingAllResults && (_page + 1) * PageSize < _total);
        AddSelectedCommand = Command(AddSelected, () => IsAttached && !IsBusy && SelectedResult != null && SelectedResult.RawValue.Length > 0);
        OpenMemoryViewerCommand = Command(() =>
        {
            ulong address = SelectedResult?.Address ?? SelectedWatch?.Address ?? 0;
            if (_engine != null && address != 0) ShowHexViewer(address);
        }, () => IsAttached && !IsBusy && (SelectedResult != null || SelectedWatch != null));
        OpenDisassemblyCommand = Command(() =>
        {
            if (_engine != null) ShowDisassembly(SelectedResult?.Address ?? SelectedWatch?.Address);
        }, () => IsAttached && !IsBusy);
        WriteSelectedCommand = Async(WriteSelectedAsync, () => IsAttached && !IsBusy && SelectedWatch != null && !string.IsNullOrWhiteSpace(EditValueText));
        RemoveWatchCommand = Command(RemoveWatch, () => !IsBusy && SelectedWatch != null);
        SaveTableCommand = Command(SaveTable, () => !IsBusy && Watches.Count > 0);
        LoadTableCommand = Command(LoadTable, () => IsAttached && !IsBusy);
        InitializeExtendedCommands();
        _watchTimer.Tick += (_, _) => { if (_watchJob?.IsCompleted != false) _watchJob = UpdateWatchesAsync(); };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _watchTimer.Start();
        RefreshProcesses();
    }
    public ProcessItem? SelectedProcess { get => _selectedProcess; set { if (Set(ref _selectedProcess, value)) RefreshCommands(); } }
    public ResultRow? SelectedResult { get => _selectedResult; set { if (Set(ref _selectedResult, value)) RefreshCommands(); } }
    public WatchRow? SelectedWatch { get => _selectedWatch; set { if (Set(ref _selectedWatch, value)) { EditValueText = value?.ValueText ?? ""; RefreshCommands(); } } }
    public Option SelectedType { get => _selectedType; set { if (value != null && Set(ref _selectedType, value)) RefreshCommands(); } }
    public Option SelectedScanMode { get => _selectedScanMode; set { if (value != null && Set(ref _selectedScanMode, value)) RefreshCommands(); } }
    public string SearchValue { get => _searchValue; set => Set(ref _searchValue, value); }
    public string StartAddress { get => _startAddress; set => Set(ref _startAddress, value); }
    public string EndAddress { get => _endAddress; set => Set(ref _endAddress, value); }
    public string EditValueText { get => _editValueText; set { if (Set(ref _editValueText, value)) RefreshCommands(); } }
    public bool AlignmentEnabled { get => _alignmentEnabled; set => Set(ref _alignmentEnabled, value); }
    public bool WritableOnly { get => _writableOnly; set => Set(ref _writableOnly, value); }
    public bool IsAttached => _engine != null;
    public int AttachedProcessId => _attachedProcessId;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RefreshCommands(); } }
    public string ProcessLabel { get => _processLabel; private set => Set(ref _processLabel, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string LogText => StatusText;
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public double ScanProgress { get => _scanProgress; private set => Set(ref _scanProgress, value); }
    public string ResultSummary => _hasScan ? $"{_total:N0} 个匹配地址" : "等待首次扫描";
    public string PageLabel => _total == 0 ? "0 / 0" : _showingAllResults ? $"全部 {_total:N0} 项" : $"{_page + 1:N0} / {(_total + PageSize - 1) / PageSize:N0}";
    private RelayCommand Command(Action action, Func<bool> canExecute)
    {
        var command = new RelayCommand(() => { try { action(); } catch (Exception ex) { StatusText = FriendlyError(ex); } }, canExecute);
        _commands.Add(command); return command;
    }
    private AsyncCommand Async(Func<Task> action, Func<bool> canExecute)
    {
        var command = new AsyncCommand(async () => { try { await action(); } catch (Exception ex) { StatusText = FriendlyError(ex); } }, canExecute);
        _commands.Add(command); return command;
    }
    private void RefreshCommands()
    {
        foreach (var command in _commands) { if (command is RelayCommand relay) relay.Refresh(); else if (command is AsyncCommand asyncCommand) asyncCommand.Refresh(); }
    }
    private static string FriendlyError(Exception ex) => ex switch
    {
        FormatException => "输入格式有误：整数和浮点数用十进制，地址和字节序列用十六进制。",
        OverflowException => "输入超出了所选类型的数值范围。",
        DllNotFoundException => "未找到 memory_core.dll，请运行根目录 build.cmd 构建完整程序。",
        _ => $"操作未完成：{ex.Message}"
    };
    private void RefreshProcesses()
    {
        int? selectedId = SelectedProcess?.Id;
        var list = new List<ProcessItem>();
        foreach (var process in Process.GetProcesses())
        {
            using (process) { try { if (process.Id > 0) list.Add(new ProcessItem(process.Id, process.ProcessName)); } catch { } }
        }
        Processes.Clear();
        foreach (var item in list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)) Processes.Add(item);
        SelectedProcess = Processes.FirstOrDefault(p => p.Id == selectedId) ?? Processes.FirstOrDefault(p => p.Name == "DemoTarget") ?? Processes.FirstOrDefault();
    }
    public void SetStatus(string text) { if (!_disposed) StatusText = text; }
    public async Task AttachToProcessAsync(int processId)
    {
        if (_disposed) return;
        if (IsBusy) { StatusText = "当前操作进行中，请完成后再选择进程。"; return; }
        try
        {
            if (processId == _attachedProcessId) { StatusText = "该窗口所属进程已经连接。"; return; }
            RefreshProcesses();
            var selected = Processes.FirstOrDefault(p => p.Id == processId);
            if (selected == null)
            {
                using var process = Process.GetProcessById(processId);
                selected = new ProcessItem(processId, process.ProcessName);
                Processes.Add(selected);
            }
            SelectedProcess = selected;
            await AttachAsync();
        }
        catch (Exception ex) { StatusText = FriendlyError(ex); }
    }
    private async Task AttachAsync()
    {
        if (SelectedProcess == null) return;
        if (_traces.Count > 0 && !await CloseTraceWindowsAsync()) return;
        var selected = SelectedProcess;
        if (Watches.Count > 0 && MessageBox.Show("连接新进程将清空当前扫描和地址表。是否继续？", "切换进程", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        IsBusy = true;
        NativeEngine? replacement = null;
        try
        {
            if (_watchJob != null) await _watchJob;
            int pid = selected.Id;
            replacement = await Task.Run(() => new NativeEngine(pid));
            if (_disposed) return;
            if (_processControl?.IsPaused == true) await Task.Run(_processControl.Resume);
            var old = _engine; _engine = replacement; replacement = null;
            old?.Dispose(); Watches.Clear(); SelectedWatch = null; NewScan();
            ProcessLabel = selected.DisplayName;
            _attachedProcessId = selected.Id;
            InitializeProcessFeatures(selected.Id);
            Notify(nameof(IsAttached));
            Notify(nameof(AttachedProcessId));
            StatusText = $"已连接 {selected.Name}。输入搜索值后开始首次扫描。";
        }
        finally { replacement?.Dispose(); IsBusy = false; }
    }
    private async Task LaunchDemoAsync()
    {
        string executable = BundledAssets.Resolve("DemoTarget.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("演示程序尚未构建，请运行 build.cmd。");
        var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
        if (process == null) throw new InvalidOperationException("未能启动演示程序。");
        using (process)
        {
            await Task.Delay(350);
            if (_disposed) return;
            RefreshProcesses(); SelectedProcess = Processes.FirstOrDefault(p => p.Id == process.Id);
            if (SelectedProcess == null) throw new InvalidOperationException("演示进程已退出。");
            await AttachAsync();
            if (_engine != null && _attachedProcessId == process.Id) StatusText = "演示已连接：用 4 字节整数搜索生命值 100；在演示窗口按 H 减少生命，再用「数值减少」筛选。";
        }
    }
    private void NewScan()
    {
        _resultOverrides.Clear();
        Results.Clear(); SelectedResult = null; _hasScan = false; _showingAllResults = false; _total = 0; _page = 0;
        ScanProgress = 0; ElapsedText = "等待扫描"; SelectedScanMode = ScanModes[0];
        Notify(nameof(ResultSummary)); Notify(nameof(PageLabel)); RefreshCommands();
        StatusText = IsAttached ? "扫描条件已重置。可以开始新的首次扫描。" : "选择进程并连接，或启动演示进程体验扫描。";
    }
    public async Task ScanAsync(bool next)
    {
        var engine = _engine ?? throw new InvalidOperationException("请先连接进程。");
        int type = SelectedType.Value, mode = SelectedScanMode.Value;
        if (next && (!_hasScan || type != _scanType)) throw new ArgumentException("数据类型已变化，请重新执行首次扫描。");
        if (!next && mode is not (0 or 1 or 6 or 7 or 8)) throw new ArgumentException("首次扫描支持精确、未知、大于、小于及范围搜索。");
        if (next && mode == 1) throw new ArgumentException("未知初始值仅用于首次扫描。");
        if (mode == 1 && ValueCodec.Width(type) == 0) throw new ArgumentException("字符串和字节序列需要精确搜索内容。");
        if (mode >= 4 && type >= 6) throw new ArgumentException("增加和减少仅适用于数值类型。");
        var settings = new ScanSettings();
        byte[] value = mode is 0 or >= 6 ? ValueCodec.Parse(type == 8 && mode == 0 ? 8 : type, type == 8 && mode == 0 ? "00" : SearchValue, HexDisplayEnabled) : [];
        if (type == 8 && mode == 0) { var pattern = ValueCodec.ParsePattern(SearchValue); value = pattern.Value; settings.PatternMask = pattern.Mask; }
        if (mode == 8) settings.UpperValue = ValueCodec.Parse(type, SearchUpperValue, HexDisplayEnabled);
        settings.Approximate = type is 4 or 5 && FloatToleranceEnabled;
        if (settings.Approximate)
        {
            settings.AbsoluteTolerance = double.Parse(FloatToleranceText, CultureInfo.InvariantCulture);
            settings.RelativeTolerance = double.Parse(RelativeToleranceText, CultureInfo.InvariantCulture);
            if (!double.IsFinite(settings.AbsoluteTolerance) || !double.IsFinite(settings.RelativeTolerance) || settings.AbsoluteTolerance < 0 || settings.RelativeTolerance < 0)
                throw new ArgumentException("浮点容差需要非负有限数值。");
        }
        int size = next ? _scanSize : (value.Length > 0 ? value.Length : ValueCodec.Width(type));
        if (next && mode == 0 && value.Length != size) throw new ArgumentException("再次扫描的字符串或字节长度需要保持一致。");
        ulong start = ValueCodec.Address(StartAddress), end = ValueCodec.Address(EndAddress);
        if (end != 0 && end <= start) throw new ArgumentException("结束地址必须大于起始地址（结束地址不包含在范围内）。");
        var request = new ScanRequest { Type = (uint)type, Mode = (uint)mode, StartAddress = start, EndAddress = end, Alignment = (uint)(AlignmentEnabled ? Math.Max(1, ValueCodec.Width(type)) : 1), MaxResults = 2_000_000, WritableOnly = WritableOnly ? 1u : 0u };
        var previousUi = CaptureScanUi();
        IsBusy = true; ScanProgress = 0; StatusText = next ? "正在筛选上次扫描的候选地址…" : "正在扫描可读取的内存区域…"; _progressTimer.Start();
        try
        {
            if (_watchJob != null) await _watchJob;
            int status = await Task.Run(() => engine.Scan(request, value, next, settings));
            if (_disposed) return;
            UpdateProgress();
            if (status == 5) { StatusText = "扫描已取消，上次成功的结果已保留。"; return; }
            if (status != 0) { string error = engine.LastScanError; throw new InvalidOperationException(status == 6 ? "候选地址超过 2,000,000 个。请缩小地址范围或使用更精确的条件；上次结果已保留。" : error); }
            _resultOverrides.Clear();
            RememberScanUi(previousUi);
            _scanType = type; _scanSize = size; _hasScan = true; _showingAllResults = false; _page = 0; _total = engine.Count;
            await LoadPageAsync(); ScanProgress = 100;
            Notify(nameof(ResultSummary)); Notify(nameof(PageLabel));
            StatusText = _total > 0 ? $"扫描完成，找到 {_total:N0} 个地址。双击按列编辑；Ctrl+A 全选全部结果，右键批量操作。" : "未找到匹配地址。检查类型、数值或关闭「仅扫描可写区域」后重试。";
            if (mode == 1) SelectedScanMode = ScanModes[2];
        }
        finally { _progressTimer.Stop(); IsBusy = false; }
    }
    private void UpdateProgress()
    {
        if (_engine == null) return;
        var progress = _engine.Progress();
        ScanProgress = progress.TotalBytes > 0 ? Math.Clamp(100.0 * progress.ScannedBytes / progress.TotalBytes, 0, 100) : 0;
        ElapsedText = progress.ElapsedMs < 1000 ? $"{progress.ElapsedMs:N0} ms" : $"{progress.ElapsedMs / 1000:N2} s";
    }
    private async Task LoadPageAsync()
    {
        if (_engine == null) return;
        var engine = _engine; ulong offset = _page * PageSize;
        var overrides = _resultOverrides.ToDictionary(p => p.Key, p => p.Value);
        var rows = await Task.Run(() =>
        {
            var page = engine.Page(offset, PageSize, _scanType, _scanSize);
            foreach (var row in page)
                if (overrides.TryGetValue(row.SourceAddress, out var format))
                {
                    byte[] bytes;
                    try { bytes = engine.Read(format.Address, format.Size); } catch (InvalidOperationException) { bytes = []; }
                    row.Apply(format.Address, format.Type, format.Size, bytes);
                }
            return page;
        });
        foreach (var row in rows) { row.Hexadecimal = HexDisplayEnabled; row.Apply(row.Address, row.Type, row.ByteSize, row.RawValue); }
        Results.ReplaceAll(rows); _visibleResults = [];
        SelectedResult = null; Notify(nameof(PageLabel));
    }
    private async Task ChangePage(int delta)
    {
        IsBusy = true;
        try { _page = delta > 0 ? _page + 1 : _page - 1; await LoadPageAsync(); }
        finally { IsBusy = false; }
    }
    public async Task<bool> SelectAllScanResultsAsync()
    {
        if (_disposed || IsBusy || _engine == null || !_hasScan || _total == 0) return false;
        if (_showingAllResults || (ulong)Results.Count == _total) return true;
        var engine = _engine;
        var overrides = _resultOverrides.ToDictionary(p => p.Key, p => p.Value);
        IsBusy = true; ScanProgress = 0;
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        try
        {
            if (_watchJob != null) await _watchJob;
            var all = new List<ResultRow>((int)_total);
            for (ulong offset = 0; offset < _total; offset += 1000)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var page = await Task.Run(() =>
                {
                    var rows = engine.Page(offset, (uint)Math.Min(1000, _total - offset), _scanType, _scanSize);
                    foreach (var row in rows)
                        if (overrides.TryGetValue(row.SourceAddress, out var format))
                        {
                            byte[] bytes;
                            try { bytes = engine.Read(format.Address, format.Size); } catch (InvalidOperationException) { bytes = []; }
                            row.Apply(format.Address, format.Type, format.Size, bytes);
                        }
                    return rows;
                });
                all.AddRange(page);
                ScanProgress = 100.0 * all.Count / _total;
                StatusText = $"正在准备全部扫描结果：{all.Count:N0} / {_total:N0}，可点击停止取消…";
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed) return false;
            foreach (var row in all) { row.Hexadecimal = HexDisplayEnabled; row.Apply(row.Address, row.Type, row.ByteSize, row.RawValue); }
            Results.ReplaceAll(all); _visibleResults = []; SelectedResult = null; _showingAllResults = true;
            Notify(nameof(PageLabel));
            StatusText = $"已载入全部 {_total:N0} 个扫描结果，可全选后右键批量操作。";
            return true;
        }
        catch (OperationCanceledException) { StatusText = "已取消全选准备，当前结果页保持不变。"; return false; }
        catch (Exception ex) { StatusText = FriendlyError(ex); return false; }
        finally { _batchCancellation = null; IsBusy = false; }
    }
    private void AddSelected()
    {
        if (SelectedResult == null) return;
        AddResult(SelectedResult);
    }
    private void AddResult(ResultRow result)
    {
        if (result.RawValue.Length == 0) throw new InvalidOperationException("此地址当前不可读取。");
        var existing = Watches.FirstOrDefault(w => w.Address == result.Address && w.Type == result.Type);
        if (existing != null) { SelectedWatch = existing; StatusText = "此地址已在地址表中。"; return; }
        var row = new WatchRow { Address = result.Address, Type = result.Type, Size = result.ByteSize, FrozenValue = result.RawValue.ToArray(), ValueText = result.ValueText, Hexadecimal = HexDisplayEnabled, Description = $"地址 {Watches.Count + 1}" };
        Watches.Add(row); SelectedWatch = row; RefreshCommands();
        StatusText = "已加入地址表。选中地址后可修改数值，勾选锁定可持续写入保存的值。";
    }
    private async Task WriteSelectedAsync()
    {
        if (SelectedWatch != null) await WriteRecordsValueAsync([SelectedWatch], EditValueText);
    }
    private void RemoveWatch()
    {
        if (SelectedWatch == null) return;
        Watches.Remove(SelectedWatch); SelectedWatch = null; RefreshCommands();
    }
    private static (ulong Address, int Type, int Size, string Value) RecordData(object row) => row switch
    {
        ResultRow result => (result.Address, result.Type, result.ByteSize, result.ValueText),
        WatchRow watch => (watch.Address, watch.Type, watch.Size, watch.ValueText),
        _ => throw new ArgumentException("请先选择一条有效记录。")
    };
    public async Task HandleRecordsActionAsync(IReadOnlyList<object> rows, string action)
    {
        if (_disposed || rows.Count == 0) return;
        var selection = rows.Distinct().ToArray();
        if (selection.Length == 1 && action is not ("freeze" or "unfreeze"))
        {
            await HandleRecordActionAsync(selection[0], action); return;
        }
        try
        {
            if (action.StartsWith("copy-", StringComparison.Ordinal))
            {
                Clipboard.SetText(string.Join(Environment.NewLine, selection.Select(row =>
                {
                    var data = RecordData(row);
                    return action switch
                    {
                        "copy-address" => $"0x{data.Address:X16}",
                        "copy-value" => data.Value,
                        "copy-record" => $"{(row as WatchRow)?.Description ?? "扫描结果"}\t0x{data.Address:X16}\t{ValueCodec.TypeLabel(data.Type)}\t{data.Value}",
                        _ => throw new ArgumentException("未知复制操作。")
                    };
                })));
                StatusText = $"已复制 {selection.Length:N0} 条记录。"; return;
            }
            if (IsBusy) { StatusText = "当前操作尚未完成，请稍后编辑记录。"; return; }
            if (action is "edit-value" or "edit-type" or "edit-description")
            {
                var data = RecordData(selection[0]);
                var kind = action == "edit-value" ? RecordEditKind.Value : action == "edit-type" ? RecordEditKind.Type : RecordEditKind.Description;
                string initial = kind == RecordEditKind.Description ? (selection[0] as WatchRow)?.Description ?? "" : data.Value;
                var request = new RecordEditRequest(kind, initial, data.Type, data.Size, $"已选择 {selection.Length:N0} 项", HexDisplayEnabled);
                var edited = RecordEditorWindow.Edit(Application.Current.MainWindow, request);
                if (edited != null) await ApplyRecordsEditAsync(selection, kind, edited);
                return;
            }
            if (action is "hex" or "disassemble" or "trace-write" or "trace-access" or "edit-address")
            { StatusText = "此操作请只选择一条记录。"; return; }
            int completed = 0, failed = 0;
            IsBusy = true;
            using var cancellation = new CancellationTokenSource();
            _batchCancellation = cancellation;
            try
            {
                if (_watchJob != null) await _watchJob;
                if (action == "add-watch")
                {
                    var existing = Watches.Select(w => (w.Address, w.Type)).ToHashSet();
                    var additions = new List<WatchRow>();
                    int skipped = 0;
                    foreach (var row in selection.OfType<ResultRow>())
                    {
                        if (cancellation.IsCancellationRequested || _disposed) break;
                        if (row.RawValue.Length == 0) failed++;
                        else if (!existing.Add((row.Address, row.Type))) skipped++;
                        else
                        {
                            additions.Add(new WatchRow { Address = row.Address, Type = row.Type, Size = row.ByteSize,
                                FrozenValue = row.RawValue.ToArray(), ValueText = row.ValueText, Hexadecimal = HexDisplayEnabled, Description = $"地址 {Watches.Count + additions.Count + 1}" });
                            completed++;
                        }
                        if ((completed + failed + skipped) % 200 == 0) await Task.Yield();
                    }
                    if (_disposed) return;
                    var combined = Watches.Concat(additions).ToArray();
                    Watches.ReplaceAll(combined);
                    SelectedWatch = additions.LastOrDefault() ?? SelectedWatch;
                    StatusText = $"{(cancellation.IsCancellationRequested ? "已停止添加" : "批量添加完成")}：新增 {completed:N0} 项，已存在 {skipped:N0} 项，不可读取 {failed:N0} 项。";
                    return;
                }
                if (action == "remove-watch")
                {
                    var remove = selection.OfType<WatchRow>().ToHashSet();
                    foreach (var watch in remove) watch.IsFrozen = false;
                    var remaining = Watches.Where(w => !remove.Contains(w)).ToArray();
                    completed = Watches.Count - remaining.Length;
                    Watches.ReplaceAll(remaining);
                    if (SelectedWatch != null && remove.Contains(SelectedWatch)) SelectedWatch = null;
                    StatusText = $"已从地址表移除 {completed:N0} 项。";
                    return;
                }
                foreach (var row in selection)
                {
                    if (cancellation.IsCancellationRequested || _disposed) break;
                    try
                    {
                        if (action == "add-watch" && row is ResultRow result) { AddResult(result); completed++; }
                        else if (row is WatchRow watch)
                        {
                            if (action == "remove-watch") { watch.IsFrozen = false; Watches.Remove(watch); if (SelectedWatch == watch) SelectedWatch = null; completed++; }
                            else if (action is "unfreeze" or "freeze")
                            {
                                if (action == "freeze")
                                {
                                    var engine = _engine ?? throw new InvalidOperationException("进程连接已关闭。");
                                    byte[] bytes = await Task.Run(() => engine.Read(watch.Address, watch.Size));
                                    watch.FrozenValue = bytes; watch.ValueText = FormatValue(watch.Type, bytes);
                                }
                                watch.IsFrozen = action == "freeze"; completed++;
                            }
                        }
                    }
                    catch { failed++; }
                }
                StatusText = $"{(cancellation.IsCancellationRequested ? "已停止" : "批量操作完成")}：成功 {completed:N0} 项" + (failed > 0 ? $"，失败 {failed:N0} 项（地址不可读取或连接已失效）。" : "。");
            }
            finally { _batchCancellation = null; IsBusy = false; }
        }
        catch (Exception ex) { StatusText = FriendlyError(ex); }
    }
    public async Task ApplyRecordsEditAsync(IReadOnlyList<object> rows, RecordEditKind kind, RecordEditResult edited)
    {
        if (_disposed || IsBusy) throw new InvalidOperationException("当前无法编辑记录。");
        if (kind == RecordEditKind.Address) throw new ArgumentException("批量修改地址可能合并记录，请逐条修改地址。");
        var plans = rows.Distinct().Select(row =>
        {
            var data = RecordData(row);
            var request = new RecordEditRequest(kind, data.Value, data.Type, data.Size, $"0x{data.Address:X16}", edited.Hexadecimal);
            RecordEditResult value;
            try { value = RecordEditorWindow.Validate(request, edited.Text, edited.Type, edited.ByteSize); }
            catch (ArgumentException ex) { throw new ArgumentException($"0x{data.Address:X16}：{ex.Message} 尚未写入任何记录。"); }
            if (kind == RecordEditKind.Description && row is not WatchRow) throw new ArgumentException("仅地址表支持描述。");
            int type = kind == RecordEditKind.Type ? value.Type : data.Type;
            int size = kind == RecordEditKind.Type ? value.ByteSize : data.Size;
            byte[] bytes = kind == RecordEditKind.Value ? ValueCodec.Parse(type, value.Text, edited.Hexadecimal) : [];
            return (Row: row, data.Address, Type: type, Size: size, Value: value, Bytes: bytes, Before: kind == RecordEditKind.Value ? null : CaptureRecord(row));
        }).ToArray();
        var engine = _engine ?? throw new InvalidOperationException("请先连接进程。");
        IsBusy = true; ScanProgress = 0;
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        try
        {
            if (_watchJob != null) await _watchJob;
            int completed = 0, failed = 0;
            var changedMetadata = new List<RecordState>();
            using var undoBatch = kind == RecordEditKind.Value ? _editHistory?.BeginBatch($"批量写入 {plans.Length:N0} 条记录") : null;
            string? firstError = null;
            foreach (var plan in plans)
            {
                if (cancellation.IsCancellationRequested || _disposed) break;
                try
                {
                    byte[] bytes = plan.Bytes;
                    if (kind == RecordEditKind.Value) await Task.Run(() => _editHistory!.Write(engine, plan.Address, bytes, "写入记录"));
                    else if (kind == RecordEditKind.Type) bytes = await Task.Run(() => engine.Read(plan.Address, plan.Size));
                    if (_disposed) break;
                    if (plan.Row is ResultRow result)
                    {
                        result.Apply(plan.Address, plan.Type, plan.Size, bytes);
                        if (kind == RecordEditKind.Type) _resultOverrides[result.SourceAddress] = (plan.Address, plan.Type, plan.Size);
                    }
                    else if (plan.Row is WatchRow watch)
                    {
                        if (kind == RecordEditKind.Description) watch.Description = plan.Value.Text;
                        else
                        {
                            if (kind == RecordEditKind.Type) watch.IsFrozen = false;
                            watch.Type = plan.Type; watch.Size = plan.Size; watch.FrozenValue = bytes; watch.ValueText = FormatValue(plan.Type, bytes);
                            if (SelectedWatch == watch) EditValueText = watch.ValueText;
                        }
                    }
                    completed++;
                    if (kind != RecordEditKind.Value) changedMetadata.Add(plan.Before!);
                }
                catch (Exception ex) { failed++; firstError ??= ex.Message; }
                ScanProgress = 100.0 * (completed + failed) / plans.Length;
                StatusText = $"批量编辑：已处理 {completed + failed:N0} / {plans.Length:N0} 项…";
            }
            StatusText = $"{(cancellation.IsCancellationRequested ? "已停止" : "批量编辑完成")}：成功 {completed:N0} / {plans.Length:N0} 项" +
                (failed > 0 ? $"，失败 {failed:N0} 项；{firstError}" : "。");
            if (changedMetadata.Count > 0) RememberRecords("批量修改记录", changedMetadata);
        }
        finally { _batchCancellation = null; IsBusy = false; }
    }
    public async Task HandleRecordActionAsync(object row, string action)
    {
        if (_disposed) return;
        try
        {
            var data = RecordData(row);
            if (action.StartsWith("copy-", StringComparison.Ordinal))
            {
                string text = action switch
                {
                    "copy-address" => $"0x{data.Address:X16}",
                    "copy-value" => data.Value,
                    "copy-record" => $"{(row as WatchRow)?.Description ?? "扫描结果"}\t0x{data.Address:X16}\t{ValueCodec.TypeLabel(data.Type)}\t{data.Value}",
                    _ => throw new ArgumentException("未知复制操作。")
                };
                Clipboard.SetText(text); StatusText = "已复制到剪贴板。"; return;
            }
            if (IsBusy) { StatusText = "当前操作尚未完成，请稍后编辑记录。"; return; }
            var engine = _engine ?? throw new InvalidOperationException("请先连接进程。");
            if (action.StartsWith("edit-", StringComparison.Ordinal))
            {
                RecordEditKind kind = action switch { "edit-value" => RecordEditKind.Value, "edit-address" => RecordEditKind.Address, "edit-type" => RecordEditKind.Type, "edit-description" => RecordEditKind.Description, _ => throw new ArgumentException("未知编辑操作。") };
                if (kind == RecordEditKind.Description && row is not WatchRow) return;
                string initial = kind switch { RecordEditKind.Address => $"0x{data.Address:X16}", RecordEditKind.Description => ((WatchRow)row).Description, _ => data.Value };
                var request = new RecordEditRequest(kind, initial, data.Type, data.Size, $"0x{data.Address:X16}", HexDisplayEnabled);
                var edited = RecordEditorWindow.Edit(Application.Current.MainWindow, request);
                if (edited != null) await ApplyRecordEditAsync(row, kind, edited);
                return;
            }
            switch (action)
            {
                case "add-watch" when row is ResultRow result: AddResult(result); break;
                case "remove-watch" when row is WatchRow watch:
                    watch.IsFrozen = false; Watches.Remove(watch);
                    if (SelectedWatch == watch) SelectedWatch = null;
                    RefreshCommands(); StatusText = "地址已从监视表移除。"; break;
                case "toggle-freeze" when row is WatchRow watch:
                    if (watch.IsFrozen) { watch.IsFrozen = false; StatusText = "已取消冻结。"; }
                    else
                    {
                        IsBusy = true;
                        try
                        {
                            if (_watchJob != null) await _watchJob;
                            byte[] bytes = await Task.Run(() => engine.Read(watch.Address, watch.Size));
                            watch.FrozenValue = bytes; watch.ValueText = FormatValue(watch.Type, bytes); watch.IsFrozen = true;
                            StatusText = "已冻结此地址的当前值。";
                        }
                        finally { IsBusy = false; }
                    }
                    break;
                case "hex": ShowHexViewer(data.Address); break;
                case "disassemble": ShowDisassembly(data.Address); break;
                case "trace-write":
                case "trace-access":
                    if (IsPaused) { _processControl!.Resume(); Notify(nameof(IsPaused)); Notify(nameof(PauseButtonText)); }
                    if (_traces.Count > 0) throw new InvalidOperationException("此进程已有来源追踪窗口，请先停止并关闭它。");
                    var trace = new AccessTraceWindow(_attachedProcessId, data.Address, data.Size, action == "trace-write") { Owner = Application.Current.MainWindow };
                    trace.BeforeStart = async () =>
                    {
                        if (_processControl?.IsPaused == true) await Task.Run(_processControl.Resume);
                        Notify(nameof(IsPaused)); Notify(nameof(PauseButtonText));
                    };
                    ulong first = data.Address & ~4095UL, last = (data.Address + (ulong)data.Size - 1) | 4095UL;
                    _traces.Add((trace, first, last));
                    trace.Closed += (_, _) => { _traces.RemoveAll(t => t.Window == trace); RefreshCommands(); };
                    trace.Show(); RefreshCommands(); StatusText = "来源追踪已在独立窗口打开；主窗口可继续操作，相关保护页的监视暂时跳过。";
                    break;
            }
        }
        catch (Exception ex) { StatusText = FriendlyError(ex); }
    }
    public async Task ApplyRecordEditAsync(object row, RecordEditKind kind, RecordEditResult edited)
    {
        if (_disposed || IsBusy) throw new InvalidOperationException("当前无法编辑记录。");
        var data = RecordData(row);
        var before = CaptureRecord(row);
        var request = new RecordEditRequest(kind, data.Value, data.Type, data.Size, $"0x{data.Address:X16}", edited.Hexadecimal);
        var validated = RecordEditorWindow.Validate(request, edited.Text, edited.Type, edited.ByteSize);
        if (kind == RecordEditKind.Description)
        {
            if (row is WatchRow named) named.Description = validated.Text;
            RememberRecords("修改描述", [before]);
            StatusText = "描述已更新。"; return;
        }
        var engine = _engine ?? throw new InvalidOperationException("请先连接进程。");
        ulong address = kind == RecordEditKind.Address ? ValueCodec.Address(validated.Text) : data.Address;
        int type = kind == RecordEditKind.Type ? validated.Type : data.Type;
        int size = kind == RecordEditKind.Type ? validated.ByteSize : data.Size;
        IsBusy = true;
        try
        {
            if (_watchJob != null) await _watchJob;
            if (_disposed) return;
            byte[] bytes;
            if (kind == RecordEditKind.Value)
            {
                bytes = ValueCodec.Parse(type, validated.Text, edited.Hexadecimal);
                await Task.Run(() => _editHistory!.Write(engine, address, bytes, "写入记录"));
            }
            else bytes = await Task.Run(() => engine.Read(address, size));
            if (_disposed) return;
            if (row is ResultRow result)
            {
                result.Apply(address, type, size, bytes);
                if (kind is RecordEditKind.Type or RecordEditKind.Address) _resultOverrides[result.SourceAddress] = (address, type, size);
            }
            else if (row is WatchRow watch)
            {
                if (kind is RecordEditKind.Type or RecordEditKind.Address) watch.IsFrozen = false;
                watch.Address = address; watch.Type = type; watch.Size = size;
                if (kind == RecordEditKind.Address) { watch.AddressExpression = ""; watch.PointerOffsets = []; }
                watch.FrozenValue = bytes; watch.ValueText = FormatValue(type, bytes);
                if (SelectedWatch == watch) EditValueText = watch.ValueText;
            }
            StatusText = kind switch
            {
                RecordEditKind.Value => $"已写入 0x{address:X16}。",
                RecordEditKind.Type when row is ResultRow => "这一行的读取类型已更新；重新扫描会生成新的结果列表。",
                RecordEditKind.Address when row is ResultRow => "这一行的地址已更新；重新扫描会生成新的结果列表。",
                _ => "记录已更新，冻结已关闭。"
            };
            if (kind != RecordEditKind.Value) RememberRecords("修改记录", [before]);
        }
        finally { IsBusy = false; }
    }
    private Task UpdateWatchesAsync() => RefreshLiveValuesAsync();
    public async Task RefreshLiveValuesAsync()
    {
        if (IsBusy || _watchUpdating || _engine == null || _disposed) return;
        _watchUpdating = true;
        var engine = _engine;
        var traceRanges = _traces.Where(t => t.Window is AccessTraceWindow trace && trace.HasProtectedPages).Select(t => (t.FirstPage, t.LastPage)).ToArray();
        bool Traced(ulong address, int size)
        {
            ulong last = address > ulong.MaxValue - (ulong)Math.Max(1, size) + 1 ? ulong.MaxValue : address + (ulong)Math.Max(1, size) - 1;
            return traceRanges.Any(t => address <= t.LastPage && last >= t.FirstPage);
        }
        var snapshot = Watches.Where(w => !Traced(w.Address, w.Size)).ToArray();
        var results = (_visibleResults.Length > 0 ? _visibleResults : Results.Take(200)).Where(r => !Traced(r.Address, r.ByteSize)).ToArray();
        try
        {
            var updates = await Task.Run(() =>
            {
                AddressResolver? resolver = null; string? resolverError = null;
                if (snapshot.Any(w => w.AddressExpression.Length > 0))
                    try { resolver = new AddressResolver(engine, _attachedProcessId); }
                    catch (Exception ex) { resolverError = ex.Message; }
                var watchUpdates = snapshot.Select(w =>
                {
                    ulong address = w.Address;
                    try
                    {
                        if (w.AddressExpression.Length > 0)
                        {
                            if (resolver == null) throw new InvalidOperationException(resolverError ?? "无法初始化指针解析器。");
                            address = resolver.Resolve(w.AddressExpression, w.PointerOffsets);
                        }
                        if (Traced(address, w.Size)) return (Watch: w, Address: address, Bytes: Array.Empty<byte>(), Error: (string?)"trace");
                        if (w.IsFrozen)
                        {
                            // Resolve moving pointers before a serialized freeze write.
                            if (address != w.Address) { w.Address = address; }
                            _editHistory!.MaintainFrozen(engine, w);
                        }
                        return (Watch: w, Address: address, Bytes: engine.Read(address, w.Size), Error: (string?)null);
                    }
                    catch (Exception ex) { return (Watch: w, Address: address, Bytes: Array.Empty<byte>(), Error: ex.Message); }
                }).ToArray();
                var resultUpdates = results.Select(r =>
                {
                    try { return (Row: r, Bytes: engine.Read(r.Address, r.ByteSize)); }
                    catch { return (Row: r, Bytes: Array.Empty<byte>()); }
                }).ToArray();
                return (Watches: watchUpdates, Results: resultUpdates);
            });
            if (_disposed || engine != _engine) return;
            var currentWatches = Watches.ToHashSet();
            foreach (var update in updates.Watches)
            {
                if (!currentWatches.Contains(update.Watch)) continue;
                if (update.Error == "trace") continue;
                update.Watch.Address = update.Address;
                if (update.Error == null)
                {
                    update.Watch.ValueText = FormatValue(update.Watch.Type, update.Bytes);
                    if (!update.Watch.IsFrozen) update.Watch.FrozenValue = update.Bytes;
                }
                else { bool wasFrozen = update.Watch.IsFrozen; update.Watch.IsFrozen = false; update.Watch.ValueText = "不可读取"; if (wasFrozen) StatusText = $"锁定已停止：{update.Error}"; }
            }
            foreach (var update in updates.Results)
                update.Row.Apply(update.Row.Address, update.Row.Type, update.Row.ByteSize, update.Bytes);
            RefreshCommands();
        }
        catch (Exception ex) { if (!_disposed) StatusText = "刷新内存失败：" + ex.Message; }
        finally { _watchUpdating = false; }
    }
    private void SaveTable()
    {
        var dialog = new SaveFileDialog { Filter = "Memory Studio 地址表|*.mstable", FileName = "地址表.mstable" };
        if (dialog.ShowDialog() != true) return;
        var table = new TableFile(2, ProcessLabel, Watches.Select(w => new TableEntry($"0x{w.Address:X16}", w.Type, w.Size, w.Description, w.AddressExpression, w.PointerOffsets)).ToList());
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true }));
        StatusText = "地址表已保存。绝对地址在进程重启后可能变化，加载时会检查可读性。";
    }
    private void LoadTable()
    {
        var dialog = new OpenFileDialog { Filter = "Memory Studio 地址表|*.mstable" };
        if (dialog.ShowDialog() != true) return;
        var file = new FileInfo(dialog.FileName);
        if (file.Length > 512L * 1024 * 1024) throw new ArgumentException("地址表文件超过 512 MB，请拆分后加载。");
        var table = JsonSerializer.Deserialize<TableFile>(File.ReadAllText(dialog.FileName)) ?? throw new ArgumentException("地址表格式无效。");
        if (table.Version is not (1 or 2) || table.Entries == null || table.Entries.Count > 2_000_000) throw new ArgumentException("地址表版本或条目数量无效。");
        var pending = new List<WatchRow>();
        foreach (var entry in table.Entries)
        {
            if (entry.Type < 0 || entry.Type > 8 || entry.Size < 1 || entry.Size > 4096 || entry.Type == 7 && (entry.Size & 1) != 0 || (ValueCodec.Width(entry.Type) > 0 && ValueCodec.Width(entry.Type) != entry.Size) || (entry.Offsets?.Length ?? 0) > 32) throw new ArgumentException("地址表的数据类型或长度无效。");
            ulong address = 0;
            if (string.IsNullOrWhiteSpace(entry.Expression))
            { address = ValueCodec.Address(entry.Address); if (address == 0) throw new ArgumentException("地址不能为零。"); }
            else try { address = new AddressResolver(_engine!, _attachedProcessId).Resolve(entry.Expression, entry.Offsets); }
                 catch (InvalidOperationException) { /* Preserve a module/pointer record so a future refresh can resolve it. */ }
            byte[] bytes = [];
            try { bytes = _engine!.Read(address, entry.Size); } catch { }
            pending.Add(new WatchRow { Address = address, Type = entry.Type, Size = entry.Size, Description = entry.Description ?? "未命名地址", AddressExpression = entry.Expression ?? "", PointerOffsets = entry.Offsets ?? [], Hexadecimal = HexDisplayEnabled, FrozenValue = bytes.Length > 0 ? bytes : new byte[entry.Size], ValueText = bytes.Length > 0 ? FormatValue(entry.Type, bytes) : "不可读取" });
        }
        var known = Watches.Select(w => (w.Address, w.Type)).ToHashSet();
        var combined = Watches.Concat(pending.Where(w => known.Add((w.Address, w.Type)))).ToArray();
        Watches.ReplaceAll(combined);
        RefreshCommands(); StatusText = $"已加载 {pending.Count} 个条目；锁定默认关闭。进程重启后请重新扫描确认地址。";
    }
    public void Dispose()
    {
        _disposed = true; _watchTimer.Stop(); _progressTimer.Stop();
        foreach (var row in _subscribedWatches) { row.PropertyChanged -= WatchFreezeChanged; _editHistory?.UntrackWatch(row); }
        _subscribedWatches.Clear(); _processControl?.Dispose(); _engine?.Cancel(); _engine?.Dispose(); _engine = null; _attachedProcessId = 0;
    }
}
