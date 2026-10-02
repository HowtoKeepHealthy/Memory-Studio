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

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const uint PageSize = 200;
    private NativeEngine? _engine;
    private int _attachedProcessId;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Task? _watchJob;
    private bool _watchUpdating, _disposed, _isBusy, _hasScan;
    private int _scanType = 2, _scanSize = 4;
    private ulong _page, _total;
    private ProcessItem? _selectedProcess;
    private ResultRow? _selectedResult;
    private WatchRow? _selectedWatch;
    private Option _selectedType = ValueCodec.Types[0];
    private Option _selectedScanMode;
    private string _searchValue = "100", _startAddress = "", _endAddress = "", _editValueText = "";
    private string _processLabel = "尚未连接进程", _statusText = "选择进程并连接，或启动演示进程体验扫描。", _elapsedText = "等待扫描";
    private bool _alignmentEnabled = true, _writableOnly = true;
    private double _scanProgress;
    private readonly List<object> _commands = [];
    public ObservableCollection<ProcessItem> Processes { get; } = [];
    public ObservableCollection<ResultRow> Results { get; } = [];
    public ObservableCollection<WatchRow> Watches { get; } = [];
    public IReadOnlyList<Option> TypeOptions => ValueCodec.Types;
    public IReadOnlyList<Option> ScanModes { get; } = [new("精确数值", 0), new("未知初始值", 1), new("数值发生变化", 2), new("数值保持不变", 3), new("数值增加", 4), new("数值减少", 5)];
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
        FirstScanCommand = Async(() => ScanAsync(false), () => IsAttached && !IsBusy && SelectedScanMode.Value <= 1);
        NextScanCommand = Async(() => ScanAsync(true), () => IsAttached && _hasScan && !IsBusy && SelectedScanMode.Value != 1);
        NewScanCommand = Command(NewScan, () => !IsBusy);
        CancelScanCommand = Command(() => { _engine?.Cancel(); StatusText = "正在取消扫描…"; }, () => IsBusy);
        PreviousPageCommand = Async(() => ChangePage(-1), () => !IsBusy && _page > 0);
        NextPageCommand = Async(() => ChangePage(1), () => !IsBusy && (_page + 1) * PageSize < _total);
        AddSelectedCommand = Command(AddSelected, () => IsAttached && !IsBusy && SelectedResult != null && SelectedResult.RawValue.Length > 0);
        OpenMemoryViewerCommand = Command(() =>
        {
            ulong address = SelectedResult?.Address ?? SelectedWatch?.Address ?? 0;
            if (_engine != null && address != 0) new HexViewerWindow(_engine, address) { Owner = Application.Current.MainWindow }.Show();
        }, () => IsAttached && !IsBusy && (SelectedResult != null || SelectedWatch != null));
        OpenDisassemblyCommand = Command(() =>
        {
            if (_engine != null) new DisassemblyWindow(_engine, _attachedProcessId, SelectedResult?.Address ?? SelectedWatch?.Address) { Owner = Application.Current.MainWindow }.Show();
        }, () => IsAttached && !IsBusy);
        WriteSelectedCommand = Async(WriteSelectedAsync, () => IsAttached && !IsBusy && SelectedWatch != null && !string.IsNullOrWhiteSpace(EditValueText));
        RemoveWatchCommand = Command(RemoveWatch, () => !IsBusy && SelectedWatch != null);
        SaveTableCommand = Command(SaveTable, () => !IsBusy && Watches.Count > 0);
        LoadTableCommand = Command(LoadTable, () => IsAttached && !IsBusy);
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
    public string PageLabel => _total == 0 ? "0 / 0" : $"{_page + 1:N0} / {(_total + PageSize - 1) / PageSize:N0}";
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
            var old = _engine; _engine = replacement; replacement = null;
            old?.Dispose(); Watches.Clear(); SelectedWatch = null; NewScan();
            ProcessLabel = selected.DisplayName;
            _attachedProcessId = selected.Id;
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
        Results.Clear(); SelectedResult = null; _hasScan = false; _total = 0; _page = 0;
        ScanProgress = 0; ElapsedText = "等待扫描"; SelectedScanMode = ScanModes[0];
        Notify(nameof(ResultSummary)); Notify(nameof(PageLabel)); RefreshCommands();
        StatusText = IsAttached ? "扫描条件已重置。可以开始新的首次扫描。" : "选择进程并连接，或启动演示进程体验扫描。";
    }
    public async Task ScanAsync(bool next)
    {
        var engine = _engine ?? throw new InvalidOperationException("请先连接进程。");
        int type = SelectedType.Value, mode = SelectedScanMode.Value;
        if (next && (!_hasScan || type != _scanType)) throw new ArgumentException("数据类型已变化，请重新执行首次扫描。");
        if (!next && mode > 1) throw new ArgumentException("首次扫描请选择「精确数值」或「未知初始值」。");
        if (next && mode == 1) throw new ArgumentException("未知初始值仅用于首次扫描。");
        if (mode == 1 && ValueCodec.Width(type) == 0) throw new ArgumentException("字符串和字节序列需要精确搜索内容。");
        if (mode >= 4 && type >= 6) throw new ArgumentException("增加和减少仅适用于数值类型。");
        byte[] value = mode == 0 ? ValueCodec.Parse(type, SearchValue) : [];
        int size = next ? _scanSize : (mode == 0 ? value.Length : ValueCodec.Width(type));
        if (next && mode == 0 && value.Length != size) throw new ArgumentException("再次扫描的字符串或字节长度需要保持一致。");
        ulong start = ValueCodec.Address(StartAddress), end = ValueCodec.Address(EndAddress);
        if (end != 0 && end <= start) throw new ArgumentException("结束地址必须大于起始地址（结束地址不包含在范围内）。");
        var request = new ScanRequest { Type = (uint)type, Mode = (uint)mode, StartAddress = start, EndAddress = end, Alignment = (uint)(AlignmentEnabled ? Math.Max(1, ValueCodec.Width(type)) : 1), MaxResults = 2_000_000, WritableOnly = WritableOnly ? 1u : 0u };
        IsBusy = true; ScanProgress = 0; StatusText = next ? "正在筛选上次扫描的候选地址…" : "正在扫描可读取的内存区域…"; _progressTimer.Start();
        try
        {
            if (_watchJob != null) await _watchJob;
            int status = await Task.Run(() => engine.Scan(request, value, next));
            if (_disposed) return;
            UpdateProgress();
            if (status == 5) { StatusText = "扫描已取消，上次成功的结果已保留。"; return; }
            if (status != 0) { string error = engine.LastScanError; throw new InvalidOperationException(status == 6 ? "候选地址超过 2,000,000 个。请缩小地址范围或使用更精确的条件；上次结果已保留。" : error); }
            _scanType = type; _scanSize = size; _hasScan = true; _page = 0; _total = engine.Count;
            await LoadPageAsync(); ScanProgress = 100;
            Notify(nameof(ResultSummary)); Notify(nameof(PageLabel));
            StatusText = _total > 0 ? $"扫描完成，找到 {_total:N0} 个地址。双击结果可添加到地址表。" : "未找到匹配地址。检查类型、数值或关闭「仅扫描可写区域」后重试。";
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
        var rows = await Task.Run(() => engine.Page(offset, PageSize, _scanType, _scanSize));
        Results.Clear(); foreach (var row in rows) Results.Add(row);
        SelectedResult = null; Notify(nameof(PageLabel));
    }
    private async Task ChangePage(int delta)
    {
        IsBusy = true;
        try { _page = delta > 0 ? _page + 1 : _page - 1; await LoadPageAsync(); }
        finally { IsBusy = false; }
    }
    private void AddSelected()
    {
        if (SelectedResult == null) return;
        var existing = Watches.FirstOrDefault(w => w.Address == SelectedResult.Address && w.Type == _scanType);
        if (existing != null) { SelectedWatch = existing; StatusText = "此地址已在地址表中。"; return; }
        var row = new WatchRow { Address = SelectedResult.Address, Type = _scanType, Size = _scanSize, FrozenValue = SelectedResult.RawValue.ToArray(), ValueText = SelectedResult.ValueText, Description = $"地址 {Watches.Count + 1}" };
        Watches.Add(row); SelectedWatch = row; RefreshCommands();
        StatusText = "已加入地址表。选中地址后可修改数值，勾选锁定可持续写入保存的值。";
    }
    private async Task WriteSelectedAsync()
    {
        var watch = SelectedWatch; var engine = _engine;
        if (watch == null || engine == null) return;
        var value = ValueCodec.Parse(watch.Type, EditValueText);
        if (value.Length != watch.Size) throw new ArgumentException("写入长度必须与此地址的数据长度一致。");
        IsBusy = true;
        try
        {
            if (_watchJob != null) await _watchJob;
            if (_disposed) return;
            await Task.Run(() => engine.Write(watch.Address, value));
            watch.FrozenValue = value; watch.ValueText = ValueCodec.Format(watch.Type, value); StatusText = $"已写入 {watch.AddressText}。";
        }
        finally { IsBusy = false; }
    }
    private void RemoveWatch()
    {
        if (SelectedWatch == null) return;
        Watches.Remove(SelectedWatch); SelectedWatch = null; RefreshCommands();
    }
    private async Task UpdateWatchesAsync()
    {
        if (IsBusy || _watchUpdating || _engine == null || Watches.Count == 0 || _disposed) return;
        _watchUpdating = true;
        var engine = _engine; var snapshot = Watches.ToArray();
        try
        {
            var updates = await Task.Run(() => snapshot.Select(w =>
            {
                try { if (w.IsFrozen) engine.Write(w.Address, w.FrozenValue); return (Watch: w, Bytes: engine.Read(w.Address, w.Size), Error: (string?)null); }
                catch (Exception ex) { return (Watch: w, Bytes: Array.Empty<byte>(), Error: ex.Message); }
            }).ToArray());
            if (_disposed || engine != _engine) return;
            foreach (var update in updates)
            {
                if (!Watches.Contains(update.Watch)) continue;
                if (update.Error == null)
                {
                    update.Watch.ValueText = ValueCodec.Format(update.Watch.Type, update.Bytes);
                    if (!update.Watch.IsFrozen) update.Watch.FrozenValue = update.Bytes;
                }
                else { bool wasFrozen = update.Watch.IsFrozen; update.Watch.IsFrozen = false; update.Watch.ValueText = "不可读取"; if (wasFrozen) StatusText = $"锁定已停止：{update.Error}"; }
            }
        }
        finally { _watchUpdating = false; }
    }
    private void SaveTable()
    {
        var dialog = new SaveFileDialog { Filter = "Memory Studio 地址表|*.mstable", FileName = "地址表.mstable" };
        if (dialog.ShowDialog() != true) return;
        var table = new TableFile(1, ProcessLabel, Watches.Select(w => new TableEntry(w.AddressText, w.Type, w.Size, w.Description)).ToList());
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true }));
        StatusText = "地址表已保存。绝对地址在进程重启后可能变化，加载时会检查可读性。";
    }
    private void LoadTable()
    {
        var dialog = new OpenFileDialog { Filter = "Memory Studio 地址表|*.mstable" };
        if (dialog.ShowDialog() != true) return;
        var file = new FileInfo(dialog.FileName);
        if (file.Length > 2_000_000) throw new ArgumentException("地址表文件过大。");
        var table = JsonSerializer.Deserialize<TableFile>(File.ReadAllText(dialog.FileName)) ?? throw new ArgumentException("地址表格式无效。");
        if (table.Version != 1 || table.Entries == null || table.Entries.Count > 2000) throw new ArgumentException("地址表版本或条目数量无效。");
        var pending = new List<WatchRow>();
        foreach (var entry in table.Entries)
        {
            if (entry.Type < 0 || entry.Type > 8 || entry.Size < 1 || entry.Size > 4096 || (ValueCodec.Width(entry.Type) > 0 && ValueCodec.Width(entry.Type) != entry.Size)) throw new ArgumentException("地址表的数据类型或长度无效。");
            ulong address = ValueCodec.Address(entry.Address);
            if (address == 0) throw new ArgumentException("地址不能为零。");
            byte[] bytes = [];
            try { bytes = _engine!.Read(address, entry.Size); } catch { }
            pending.Add(new WatchRow { Address = address, Type = entry.Type, Size = entry.Size, Description = entry.Description ?? "未命名地址", FrozenValue = bytes.Length > 0 ? bytes : new byte[entry.Size], ValueText = bytes.Length > 0 ? ValueCodec.Format(entry.Type, bytes) : "不可读取" });
        }
        foreach (var watch in pending) if (!Watches.Any(w => w.Address == watch.Address && w.Type == watch.Type)) Watches.Add(watch);
        RefreshCommands(); StatusText = $"已加载 {pending.Count} 个条目；锁定默认关闭。进程重启后请重新扫描确认地址。";
    }
    public void Dispose()
    {
        _disposed = true; _watchTimer.Stop(); _progressTimer.Stop(); _engine?.Cancel(); _engine?.Dispose(); _engine = null; _attachedProcessId = 0;
    }
}
