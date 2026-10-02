using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MemoryStudio;

public partial class DisassemblyWindow : Window
{
    private const int WindowBytes = 512;
    private readonly NativeEngine _engine;
    private readonly int _processId;
    private readonly ulong? _initialAddress;
    private readonly Stack<ulong> _history = new();
    private ProcessInspection? _inspection;
    private DisassemblyReadResult? _result;
    private bool _constructing = true, _loading, _closed, _ready;

    public DisassemblyWindow(NativeEngine engine, int processId, ulong? initialAddress = null)
    {
        InitializeComponent();
        _engine = engine;
        _processId = processId;
        _initialAddress = initialAddress;
        if (initialAddress.HasValue) AddressInput.Text = $"0x{initialAddress.Value:X16}";
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _closed = true;
        PreviewKeyDown += Window_PreviewKeyDown;
        _constructing = false;
        SetInputEnabled(false);
    }

    private async Task InitializeAsync()
    {
        await RefreshModulesAsync();
        if (_closed || !_ready) return;
        ulong? start = _initialAddress ?? (_inspection?.Modules.FirstOrDefault()?.EntryPoint);
        if (start.HasValue) await NavigateAsync(start.Value, false);
        else StatusLabel.Text = "目标模块没有可用入口。请选择模块或输入十六进制地址。";
    }

    private async Task RefreshModulesAsync()
    {
        if (_loading || _closed) return;
        _loading = true;
        SetInputEnabled(false);
        StatusLabel.Text = _engine.Progress().Running != 0 ? "扫描正在进行，模块信息将在扫描结束后读取…" : "正在查询进程位数及已加载模块…";
        ulong? selectedBase = (ModuleCombo.SelectedItem as ProcessModuleInfo)?.BaseAddress;
        try
        {
            var inspection = await Task.Run(() => ProcessInspector.Inspect(_processId, _engine));
            if (_closed) return;
            _inspection = inspection;
            ModuleCombo.ItemsSource = inspection.Modules;
            ModuleCombo.SelectedItem = inspection.Modules.FirstOrDefault(m => m.BaseAddress == selectedBase) ?? inspection.Modules.FirstOrDefault();
            ArchitectureLabel.Text = $"PID {_processId}  ·  检测：{inspection.Architecture.Label}  ·  {inspection.Modules.Count:N0} 个已加载模块";
            ArchitectureLabel.ToolTip = inspection.Architecture.Warning ?? inspection.Warning;
            _ready = true;
            StatusLabel.Text = inspection.Warning ?? inspection.Architecture.Warning ?? "模块已更新。选择入口或输入地址读取指令。";
        }
        catch (ObjectDisposedException) { if (!_closed) ShowConnectionClosed(); }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _inspection = new(new(null, "查询失败", ex.Message), [], ex.Message);
                _ready = true;
                ArchitectureLabel.Text = $"PID {_processId}  ·  架构查询失败，请手动选择位数";
                StatusLabel.Text = $"模块信息无法读取：{ex.Message}。可手动输入地址。";
            }
        }
        finally { _loading = false; if (!_closed) SetInputEnabled(true); }
    }

    private int SelectedBitness()
    {
        int requested = int.Parse(((ComboBoxItem)BitnessCombo.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture);
        if (requested is 32 or 64) return requested;
        return _inspection?.Architecture.Bitness ?? throw new InvalidOperationException("无法自动确定 x86/x64 模式，请手动选择 32 或 64 位。ARM 指令集不能使用此查看器解码。");
    }

    private async Task NavigateAsync(ulong address, bool recordHistory = true)
    {
        if (_loading || _closed || !_ready) return;
        int bitness;
        try { bitness = SelectedBitness(); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; return; }
        _loading = true;
        SetInputEnabled(false);
        StatusLabel.Text = _engine.Progress().Running != 0 ? "扫描正在进行；指令读取将在扫描结束后继续…" : $"正在读取并解码 {bitness} 位指令…";
        try
        {
            var result = await Task.Run(() => DisassemblyService.ReadWindow(_engine, address, bitness, WindowBytes));
            if (_closed) return;
            if (recordHistory && _result != null && _result.StartAddress != address) _history.Push(_result.StartAddress);
            _result = result;
            AddressInput.Text = $"0x{address:X16}";
            InstructionGrid.ItemsSource = result.Rows;
            if (result.Rows.Count > 0) InstructionGrid.SelectedIndex = 0;
            RangeLabel.Text = $"0x{address:X16} — 0x{result.NextAddress:X16}  ·  {result.Rows.Count:N0} 条  ·  {bitness} 位";
            var module = _inspection?.Modules.FirstOrDefault(m => m.Contains(address));
            if (module != null) ModuleCombo.SelectedItem = module;
            int invalid = result.Rows.Count(row => row.IsInvalid);
            StatusLabel.Text = result.BoundaryMessage ?? $"已按指定起点解码；{invalid:N0} 条无效或截断编码。前移起点可能落在指令中间；直接分支可双击跟随。";
        }
        catch (ObjectDisposedException) { if (!_closed) ShowConnectionClosed(); }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = $"读取未完成：{ex.Message}"; }
        finally { _loading = false; if (!_closed) SetInputEnabled(true); }
    }

    private async Task ReadInputAsync()
    {
        if (_loading || _closed) return;
        string text = AddressInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address))
        {
            StatusLabel.Text = "请输入有效的十六进制地址，例如 0x00007FF600001000。";
            return;
        }
        await NavigateAsync(address);
    }

    private void SetInputEnabled(bool enabled)
    {
        ModuleCombo.IsEnabled = enabled;
        BitnessCombo.IsEnabled = enabled;
        AddressInput.IsEnabled = enabled;
        GoButton.IsEnabled = enabled && _ready;
        EntryButton.IsEnabled = enabled && (ModuleCombo.SelectedItem as ProcessModuleInfo)?.EntryPoint != null;
        BaseButton.IsEnabled = enabled && ModuleCombo.SelectedItem != null;
        ModulesButton.IsEnabled = enabled;
        PreviousButton.IsEnabled = enabled && _result?.StartAddress >= WindowBytes;
        NextButton.IsEnabled = enabled && _result != null && _result.NextAddress > _result.StartAddress;
        BackButton.IsEnabled = enabled && _history.Count > 0;
        CopyButton.IsEnabled = enabled && _result != null;
        InstructionGrid.IsEnabled = enabled;
    }

    private void ShowConnectionClosed()
    {
        _ready = false;
        InstructionGrid.ItemsSource = null;
        StatusLabel.Text = "进程连接已经关闭或更换，请从当前进程重新打开反汇编查看器。";
    }

    private void ModuleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_constructing) return;
        EntryButton.IsEnabled = !_loading && (ModuleCombo.SelectedItem as ProcessModuleInfo)?.EntryPoint != null;
        BaseButton.IsEnabled = !_loading && ModuleCombo.SelectedItem != null;
        if (ModuleCombo.SelectedItem is ProcessModuleInfo module)
            ModuleCombo.ToolTip = module.EntryPoint is ulong entry ? $"入口：0x{entry:X16}" : "此模块没有可读取的 PE 入口点。";
    }
    private async void BitnessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_constructing || !_ready || _loading || _closed || !IsLoaded || _result == null) return;
        await NavigateAsync(_result.StartAddress, false);
    }
    private async void GoButton_Click(object sender, RoutedEventArgs e) => await ReadInputAsync();
    private async void ModulesButton_Click(object sender, RoutedEventArgs e) => await RefreshModulesAsync();
    private async void EntryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((ModuleCombo.SelectedItem as ProcessModuleInfo)?.EntryPoint is ulong entry) await NavigateAsync(entry);
    }
    private async void BaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModuleCombo.SelectedItem is ProcessModuleInfo module) await NavigateAsync(module.BaseAddress);
    }
    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.StartAddress >= WindowBytes) await NavigateAsync(_result.StartAddress - WindowBytes);
    }
    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result != null && _result.NextAddress > _result.StartAddress) await NavigateAsync(_result.NextAddress);
    }
    private async Task BackAsync()
    {
        if (_loading || _history.Count == 0) return;
        ulong previous = _history.Peek();
        await NavigateAsync(previous, false);
        if (_result?.StartAddress == previous) _history.Pop();
        if (!_closed) SetInputEnabled(!_loading);
    }
    private async void BackButton_Click(object sender, RoutedEventArgs e) => await BackAsync();
    private async void AddressInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ReadInputAsync();
    }
    private async Task FollowSelectedAsync()
    {
        if (InstructionGrid.SelectedItem is DisassemblyRow { BranchTarget: ulong target }) await NavigateAsync(target);
        else StatusLabel.Text = "此指令没有可跟随的直接分支目标；间接分支需要运行时寄存器或内存值。";
    }
    private async void FollowBranch_Click(object sender, RoutedEventArgs e) => await FollowSelectedAsync();
    private async void InstructionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(InstructionGrid, e.OriginalSource as DependencyObject) is not DataGridRow) return;
        e.Handled = true;
        await FollowSelectedAsync();
    }
    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelected();
    private void CopySelected()
    {
        var selected = InstructionGrid.SelectedItems.Cast<DisassemblyRow>().OrderBy(row => row.Address).ToArray();
        if (selected.Length == 0) { StatusLabel.Text = "请先选择需要复制的指令。"; return; }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, selected.Select(row => $"{row.AddressText}  {row.BytesText,-44}  {row.Instruction}")));
            StatusLabel.Text = $"已复制 {selected.Length:N0} 条指令。";
        }
        catch (Exception ex) { StatusLabel.Text = $"复制未完成：{ex.Message}"; }
    }
    private async void InstructionGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; CopySelected(); }
        else if (e.Key == Key.Enter) { e.Handled = true; await FollowSelectedAsync(); }
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        { e.Handled = true; await BackAsync(); }
    }
}
