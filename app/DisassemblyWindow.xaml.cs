using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MemoryStudio;

public partial class DisassemblyWindow : Window
{
    private const int WindowBytes = 512, MaximumRows = 4096;
    private readonly NativeEngine _engine;
    private readonly int _processId;
    private readonly ulong? _initialAddress, _relatedAddress;
    private readonly bool _ownsEngine;
    private readonly MemoryEditHistory _edits;
    private readonly ObservableCollection<DisassemblyRow> _rows = new();
    private readonly SortedSet<ulong> _anchors = new();
    private readonly Dictionary<ulong, byte[]> _originalCode = new();
    private readonly Stack<ulong> _history = new();
    private ProcessInspection? _inspection;
    private ScrollViewer? _scroll;
    private ulong _focusAddress;
    private int _loadedBitness;
    private bool _constructing = true, _loading, _closed, _ready, _suppressScroll, _beforeBlocked, _afterBlocked;
    private bool _wheelLoading;
    private double _queuedWheelMovement;

    public DisassemblyWindow(NativeEngine engine, int processId, ulong? initialAddress = null, ulong? relatedAddress = null, bool ownsEngine = false)
    {
        InitializeComponent();
        _engine = engine; _processId = processId; _initialAddress = initialAddress; _relatedAddress = relatedAddress; _ownsEngine = ownsEngine;
        _edits = MemoryEditHistory.ForProcess(processId);
        InstructionGrid.ItemsSource = _rows;
        if (initialAddress.HasValue) AddressInput.Text = $"0x{initialAddress.Value:X16}";
        DataButton.Content = relatedAddress.HasValue ? "触发数据内存" : "选中地址内存";
        DataButton.ToolTip = relatedAddress.HasValue ? $"查看 0x{relatedAddress.Value:X16} 附近的实时字节" : "查看选中指令地址的原始字节";
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        Loaded += async (_, _) =>
        {
            _scroll = FindChild<ScrollViewer>(InstructionGrid);
            if (_scroll != null) _scroll.ScrollChanged += Scroll_ScrollChanged;
            await InitializeAsync();
        };
        Closed += (_, _) => { _closed = true; if (_ownsEngine) _engine.Dispose(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        _constructing = false; SetInputEnabled(false);
    }
    private async Task InitializeAsync()
    {
        await RefreshModulesAsync();
        if (_closed || !_ready) return;
        ulong? start = _initialAddress ?? _inspection?.Modules.FirstOrDefault()?.EntryPoint;
        if (start.HasValue) await NavigateAsync(start.Value, false);
        else StatusLabel.Text = "目标模块没有可用入口。请选择模块或输入十六进制地址。";
    }
    private async Task RefreshModulesAsync()
    {
        if (_loading || _closed) return;
        _loading = true; SetInputEnabled(false); StatusLabel.Text = "正在查询进程位数及已加载模块…";
        ulong? selectedBase = (ModuleCombo.SelectedItem as ProcessModuleInfo)?.BaseAddress;
        try
        {
            var inspection = await Task.Run(() => ProcessInspector.Inspect(_processId, _engine));
            if (_closed) return;
            _inspection = inspection; ModuleCombo.ItemsSource = inspection.Modules;
            ModuleCombo.SelectedItem = inspection.Modules.FirstOrDefault(m => m.BaseAddress == selectedBase) ?? inspection.Modules.FirstOrDefault();
            ArchitectureLabel.Text = $"PID {_processId} · 检测：{inspection.Architecture.Label} · {inspection.Modules.Count:N0} 个模块";
            ArchitectureLabel.ToolTip = inspection.Architecture.Warning ?? inspection.Warning;
            _ready = true; StatusLabel.Text = inspection.Warning ?? inspection.Architecture.Warning ?? "输入已知指令地址后，滚动可连续浏览。";
        }
        catch (ObjectDisposedException) { if (!_closed) ShowConnectionClosed(); }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _inspection = new(new(null, "查询失败", ex.Message), [], ex.Message); _ready = true;
                ArchitectureLabel.Text = $"PID {_processId} · 架构查询失败，请手动选择位数";
                StatusLabel.Text = $"模块信息无法读取：{ex.Message}。可手动输入地址。";
            }
        }
        finally { _loading = false; if (!_closed) SetInputEnabled(true); }
    }
    private int SelectedBitness()
    {
        int requested = int.Parse(((ComboBoxItem)BitnessCombo.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture);
        return requested is 32 or 64 ? requested : _inspection?.Architecture.Bitness ??
            throw new InvalidOperationException("无法自动确定 x86/x64 模式，请手动选择 32 或 64 位。ARM 指令集不能使用此查看器解码。");
    }
    private ulong? EarlierAnchor(ulong end)
    {
        ulong desired = end > WindowBytes ? end - WindowBytes : 0;
        // Inferred reverse boundaries are never promoted to reliable anchors.
        return _anchors.Where(a => a <= desired && a < end && end - a <= 65536).Select(a => (ulong?)a).LastOrDefault();
    }
    private async Task NavigateAsync(ulong address, bool recordHistory = true)
    {
        if (_loading || _closed || !_ready) return;
        int bitness;
        try { bitness = SelectedBitness(); } catch (Exception ex) { StatusLabel.Text = ex.Message; return; }
        _loading = true; _suppressScroll = true; SetInputEnabled(false);
        StatusLabel.Text = $"正在以 0x{address:X16} 为指令起点读取 {bitness} 位代码…";
        try
        {
            if (_loadedBitness != bitness) _anchors.Clear();
            _loadedBitness = bitness;
            var forward = await Task.Run(() => DisassemblyService.ReadWindow(_engine, address, bitness, WindowBytes));
            if (_closed) return;
            DisassemblyReadResult? before = null;
            string? backwardError = null;
            ulong? anchor = EarlierAnchor(address);
            try { before = await Task.Run(() => DisassemblyService.ReadBefore(_engine, address, bitness, WindowBytes, anchor)); }
            catch (InvalidOperationException ex) when (ex is not ObjectDisposedException) { backwardError = ex.Message; }
            if (_closed) return;
            if (recordHistory && _rows.Count > 0 && _focusAddress != address) _history.Push(_focusAddress);
            _focusAddress = address; _beforeBlocked = before == null || before.Rows.Count == 0; _afterBlocked = forward.BoundaryMessage != null;
            _rows.Clear();
            if (before != null) foreach (var row in before.Rows) _rows.Add(row);
            foreach (var row in forward.Rows) _rows.Add(row);
            RememberAnchors(); AddressInput.Text = $"0x{address:X16}";
            var module = _inspection?.Modules.FirstOrDefault(m => m.Contains(address));
            if (module != null) ModuleCombo.SelectedItem = module;
            UpdateRange();
            var selected = _rows.FirstOrDefault(r => r.Address == address);
            InstructionGrid.SelectedItem = selected;
            await Dispatcher.InvokeAsync(() =>
            {
                InstructionGrid.UpdateLayout();
                if (selected != null) InstructionGrid.ScrollIntoView(selected);
                if (_scroll != null && selected != null) _scroll.ScrollToVerticalOffset(Math.Max(0, _rows.IndexOf(selected) - _scroll.ViewportHeight / 2));
            }, DispatcherPriority.Loaded);
            StatusLabel.Text = forward.BoundaryMessage ?? before?.BoundaryMessage ?? backwardError ?? "已定位指定指令；滚轮到达上下边界时继续加载。双击直接分支可跟随目标。";
        }
        catch (ObjectDisposedException) { if (!_closed) ShowConnectionClosed(); }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "读取未完成：" + ex.Message; }
        finally { _loading = false; _suppressScroll = false; if (!_closed) SetInputEnabled(true); }
    }
    public async Task LoadAdjacentAsync(bool before, bool retryBlocked = false)
    {
        if (_loading || _closed || _rows.Count == 0 || !retryBlocked && (before ? _beforeBlocked : _afterBlocked)) return;
        _loading = true; _suppressScroll = true; SetInputEnabled(false);
        double offset = _scroll?.VerticalOffset ?? 0;
        object? selected = InstructionGrid.SelectedItem;
        int inserted = 0;
        try
        {
            ulong boundary = before ? _rows[0].Address : _rows[^1].NextAddress;
            ulong? anchor = before ? EarlierAnchor(boundary) : null;
            var read = await Task.Run(() => before ? DisassemblyService.ReadBefore(_engine, boundary, _loadedBitness, WindowBytes, anchor)
                : DisassemblyService.ReadWindow(_engine, boundary, _loadedBitness, WindowBytes));
            if (_closed) return;
            if (before)
            {
                var incoming = read.Rows.Where(r => r.NextAddress <= boundary).ToArray();
                for (int i = incoming.Length - 1; i >= 0; --i) _rows.Insert(0, incoming[i]);
                inserted = incoming.Length; _beforeBlocked = inserted == 0 || _rows[0].Address == 0;
            }
            else
            {
                foreach (var row in read.Rows) _rows.Add(row);
                _afterBlocked = read.Rows.Count == 0 || read.BoundaryMessage != null;
            }
            RememberAnchors();
            int removedTop = 0;
            while (_rows.Count > MaximumRows)
            {
                if (before) { _rows.RemoveAt(_rows.Count - 1); _afterBlocked = false; }
                else { _rows.RemoveAt(0); ++removedTop; _beforeBlocked = false; }
            }
            if (selected is DisassemblyRow chosen && _rows.Contains(chosen)) InstructionGrid.SelectedItem = chosen;
            await Dispatcher.InvokeAsync(() =>
            {
                InstructionGrid.UpdateLayout();
                // Loading preserves the anchor, then consumes the wheel gesture itself.
                // Otherwise a wheel at offset zero only loads data and visibly does nothing.
                _scroll?.ScrollToVerticalOffset(Math.Max(0, offset + inserted - removedTop - (_wheelLoading ? _queuedWheelMovement : 0)));
            }, DispatcherPriority.Loaded);
            UpdateRange(); StatusLabel.Text = read.BoundaryMessage ?? $"已连续加载 {read.Rows.Count:N0} 条指令。选中项及滚动位置已保留。";
        }
        catch (ObjectDisposedException) { if (!_closed) ShowConnectionClosed(); }
        catch (Exception ex)
        {
            if (before) _beforeBlocked = true; else _afterBlocked = true;
            if (!_closed) StatusLabel.Text = "已到达无法连续读取的边界：" + ex.Message + "。刷新或跳转可重试。";
            if (_wheelLoading) _scroll?.ScrollToVerticalOffset(Math.Max(0, offset - _queuedWheelMovement));
        }
        finally { _loading = false; _suppressScroll = false; if (!_closed) SetInputEnabled(true); }
    }
    private void RememberAnchors()
    {
        foreach (var row in _rows.Where(r => !r.IsBoundaryUncertain && !r.IsInvalid)) _anchors.Add(row.Address);
        while (_anchors.Count > 32768) _anchors.Remove(_anchors.Min);
    }
    private void UpdateRange() => RangeLabel.Text = _rows.Count == 0 ? "没有可解码内容" :
        $"0x{_rows[0].Address:X16} — 0x{_rows[^1].NextAddress:X16} · {_rows.Count:N0} 条 · {_loadedBitness} 位";
    private async void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_loading || _suppressScroll || _closed || _scroll == null || e.VerticalChange == 0) return;
        if (e.VerticalChange < 0 && _scroll.VerticalOffset <= 2) await LoadAdjacentAsync(true);
        else if (e.VerticalChange > 0 && _scroll.VerticalOffset + _scroll.ViewportHeight >= _scroll.ExtentHeight - 2) await LoadAdjacentAsync(false);
    }
    private async void InstructionGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll == null || _closed) return;
        double lines = SystemParameters.WheelScrollLines;
        if (lines < 0) lines = Math.Max(1, _scroll.ViewportHeight - 1);
        if (lines == 0) return;
        double movement = e.Delta / 120.0 * lines;
        if (_loading)
        {
            if (_wheelLoading) { _queuedWheelMovement += movement; e.Handled = true; }
            return;
        }
        bool before = movement > 0;
        double predicted = _scroll.VerticalOffset - movement;
        bool edge = before ? predicted <= 2 : predicted + _scroll.ViewportHeight >= _scroll.ExtentHeight - 2;
        if (!edge) return;
        e.Handled = true;
        _wheelLoading = true; _queuedWheelMovement = movement;
        try { await LoadAdjacentAsync(before, retryBlocked: true); }
        finally { _wheelLoading = false; _queuedWheelMovement = 0; }
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); ++i)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is T nested) return nested;
        }
        return null;
    }
    private async Task ReadInputAsync()
    {
        string text = AddressInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address))
        { StatusLabel.Text = "请输入有效的十六进制指令地址。"; return; }
        await NavigateAsync(address);
    }
    private void SetInputEnabled(bool enabled)
    {
        ModuleCombo.IsEnabled = BitnessCombo.IsEnabled = AddressInput.IsEnabled = ModulesButton.IsEnabled = enabled;
        GoButton.IsEnabled = enabled && _ready;
        EntryButton.IsEnabled = enabled && (ModuleCombo.SelectedItem as ProcessModuleInfo)?.EntryPoint != null;
        BaseButton.IsEnabled = enabled && ModuleCombo.SelectedItem != null;
        PreviousButton.IsEnabled = enabled && _rows.Count > 0 && _rows[0].Address > 0;
        NextButton.IsEnabled = enabled && _rows.Count > 0 && !_afterBlocked;
        BackButton.IsEnabled = enabled && _history.Count > 0;
        CopyButton.IsEnabled = DataButton.IsEnabled = enabled && _rows.Count > 0;
        PatchButton.IsEnabled = NopButton.IsEnabled = enabled && InstructionGrid.SelectedItem is DisassemblyRow { IsBoundaryUncertain: false, IsTruncated: false };
        RestoreButton.IsEnabled = enabled && InstructionGrid.SelectedItem is DisassemblyRow row && _originalCode.ContainsKey(row.Address);
        UndoButton.IsEnabled = enabled && _edits.CanUndo;
        UndoButton.ToolTip = _edits.CanUndo ? "撤销：" + _edits.UndoDescription : "没有可撤销的修改";
    }
    private void ShowConnectionClosed() { _ready = false; _rows.Clear(); StatusLabel.Text = "进程连接已关闭，请重新打开查看器。"; }
    private void ModuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_constructing) return;
        EntryButton.IsEnabled = !_loading && (ModuleCombo.SelectedItem as ProcessModuleInfo)?.EntryPoint != null;
        BaseButton.IsEnabled = !_loading && ModuleCombo.SelectedItem != null;
    }
    private void InstructionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_constructing) SetInputEnabled(!_loading); }
    private async void BitnessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_constructing && _ready && !_loading && !_closed && IsLoaded && _rows.Count > 0) await NavigateAsync(_focusAddress, false); }
    private async void GoButton_Click(object sender, RoutedEventArgs e) => await ReadInputAsync();
    private async void ModulesButton_Click(object sender, RoutedEventArgs e) => await RefreshModulesAsync();
    private async void EntryButton_Click(object sender, RoutedEventArgs e) { if ((ModuleCombo.SelectedItem as ProcessModuleInfo)?.EntryPoint is ulong entry) await NavigateAsync(entry); }
    private async void BaseButton_Click(object sender, RoutedEventArgs e) { if (ModuleCombo.SelectedItem is ProcessModuleInfo module) await NavigateAsync(module.BaseAddress); }
    private async void PreviousButton_Click(object sender, RoutedEventArgs e) => await LoadAdjacentAsync(true, retryBlocked: true);
    private async void NextButton_Click(object sender, RoutedEventArgs e) => await LoadAdjacentAsync(false);
    private async Task BackAsync()
    {
        if (_loading || _history.Count == 0) return;
        ulong previous = _history.Peek(); await NavigateAsync(previous, false);
        if (_focusAddress == previous) _history.Pop();
        if (!_closed) SetInputEnabled(!_loading);
    }
    private async void BackButton_Click(object sender, RoutedEventArgs e) => await BackAsync();
    private async void AddressInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await ReadInputAsync(); } }
    private async Task FollowSelectedAsync()
    {
        if (InstructionGrid.SelectedItem is DisassemblyRow { BranchTarget: ulong target }) await NavigateAsync(target);
        else StatusLabel.Text = "此指令没有直接分支目标；间接分支需要运行时寄存器或内存值。";
    }
    private async void FollowBranch_Click(object sender, RoutedEventArgs e) => await FollowSelectedAsync();
    private async void InstructionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(InstructionGrid, e.OriginalSource as DependencyObject) is not DataGridRow) return;
        e.Handled = true; await FollowSelectedAsync();
    }
    private void DataButton_Click(object sender, RoutedEventArgs e)
    {
        ulong? address = _relatedAddress ?? (InstructionGrid.SelectedItem as DisassemblyRow)?.Address;
        if (!address.HasValue) return;
        try
        {
            var engine = new NativeEngine(_processId);
            try { new HexViewerWindow(engine, address.Value, _processId, ownsEngine: true) { Owner = this }.Show(); }
            catch { engine.Dispose(); throw; }
        }
        catch (Exception ex) { StatusLabel.Text = "无法打开数据内存：" + ex.Message; }
    }
    private DisassemblyRow[] PatchSelection()
    {
        var rows = InstructionGrid.SelectedItems.Cast<DisassemblyRow>().OrderBy(r => r.Address).ToArray();
        if (rows.Length == 0) throw new ArgumentException("请先选择需要修改的指令。");
        if (rows.Any(r => r.IsBoundaryUncertain || r.IsTruncated)) throw new ArgumentException("选中区域的边界未确定。请先跳转到已知指令地址，再选择需要修改的完整指令。");
        if (rows.Sum(r => r.Length) > 4096) throw new ArgumentException("一次最多修改 4096 字节。");
        for (int i = 1; i < rows.Length; ++i)
            if (rows[i - 1].NextAddress != rows[i].Address) throw new ArgumentException("请选择连续的完整指令范围。");
        return rows;
    }
    private async Task PatchAsync(DisassemblyRow[] rows, byte[] bytes, string description)
    {
        ulong address = rows[0].Address; _loading = true; SetInputEnabled(false);
        try
        {
            byte[] before = await Task.Run(() => _engine.Read(address, bytes.Length));
            await Task.Run(() => _edits.Write(_engine, address, bytes, description, code: true));
            _originalCode.TryAdd(address, before); _anchors.Clear(); _loading = false;
            await NavigateAsync(address, false);
            if (!_closed) StatusLabel.Text = description + "；页面保护已恢复、指令缓存已刷新，可撤销。";
        }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "代码修改未完成：" + ex.Message; }
        finally { _loading = false; if (!_closed) SetInputEnabled(true); }
    }
    private async void PatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            var rows = PatchSelection();
            var dialog = new MemoryBytesEditor("编辑机器码 / 汇编", rows[0].Address, rows.SelectMany(r => r.Bytes).ToArray(), _loadedBitness) { Owner = this };
            if (dialog.ShowDialog() == true) await PatchAsync(rows, dialog.EditedBytes!, "编辑机器码");
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private async void NopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { var rows = PatchSelection(); await PatchAsync(rows, Enumerable.Repeat((byte)0x90, rows.Sum(r => r.Length)).ToArray(), "用 NOP 替换选中指令"); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || InstructionGrid.SelectedItem is not DisassemblyRow row || !_originalCode.TryGetValue(row.Address, out var original)) return;
        await PatchAsync([row], original, "恢复本窗口保存的原始机器码");
    }
    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || !_edits.CanUndo) return;
        _loading = true; SetInputEnabled(false);
        try
        {
            var result = await Task.Run(() => _edits.Undo(_engine));
            _anchors.Clear(); _loading = false; await NavigateAsync(_focusAddress, false);
            StatusLabel.Text = result.Errors.Count == 0 ? "已撤销：" + result.Description : "撤销部分完成：" + string.Join("；", result.Errors);
        }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "撤销未完成：" + ex.Message; }
        finally { _loading = false; if (!_closed) SetInputEnabled(true); }
    }
    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelected();
    private void CopySelected()
    {
        var selected = InstructionGrid.SelectedItems.Cast<DisassemblyRow>().OrderBy(row => row.Address).ToArray();
        if (selected.Length == 0) { StatusLabel.Text = "请先选择指令。"; return; }
        try { Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(row => $"{row.AddressText}  {row.BytesText,-44}  {row.Instruction}  {row.Note}"))); StatusLabel.Text = $"已复制 {selected.Length:N0} 条指令。"; }
        catch (Exception ex) { StatusLabel.Text = "复制未完成：" + ex.Message; }
    }
    private async void InstructionGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; CopySelected(); }
        else if (e.Key == Key.Enter) { e.Handled = true; await FollowSelectedAsync(); }
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) { e.Handled = true; await BackAsync(); }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; UndoButton_Click(sender, e); }
        else if (e.Key == Key.F5) { e.Handled = true; await NavigateAsync(_focusAddress, false); }
    }
}
