using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MemoryStudio;

public partial class AccessTraceWindow : Window
{
    private readonly int _processId, _size;
    private readonly ulong _address;
    private readonly bool _writesOnly;
    private readonly ObservableCollection<AccessTraceHit> _rows = new();
    private readonly DispatcherTimer _timer;
    private AccessTraceService? _service;
    private bool _transition, _allowClose, _closing;
    private Task? _stopTask;
    private Task<bool>? _closeTask;
    private bool _browsing;
    private readonly HashSet<Window> _browsers = new();
    // During an asynchronous attach/detach, avoid blocking the dispatcher on the service lock.
    public bool HasProtectedPages => _transition || _service?.State.IsAttached == true;
    public Func<Task>? BeforeStart { get; set; }

    public AccessTraceWindow(int processId, ulong address, int size, bool writesOnly)
    {
        InitializeComponent();
        _processId = processId; _address = address; _size = size; _writesOnly = writesOnly;
        HeadingLabel.Text = writesOnly ? "查找写入来源" : "查找访问来源（读取/写入）";
        Title = "Memory Studio · " + HeadingLabel.Text;
        TargetLabel.Text = $"PID {processId}   /   0x{address:X16}   /   {size:N0} 字节";
        HitGrid.ItemsSource = _rows;
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += Timer_Tick;
        Closing += Window_Closing;
        SetButtons();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transition || _closing || _browsers.Count > 0) return;
        _transition = true; SetButtons();
        StatusLabel.Text = "正在调试附加并设置监控页面…";
        try
        {
            if (BeforeStart != null) await BeforeStart();
            _service?.Dispose();
            _service = new AccessTraceService(_processId, _address, _size, _writesOnly);
            await Task.Run(_service.Start);
            _rows.Clear(); RegistersText.Clear();
            _timer.Start();
            StatusLabel.Text = "正在捕获。请在目标程序中触发该地址的读取或写入。";
            UpdateStatistics();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "无法开始：" + ex.Message;
            try { _service?.Dispose(); } catch { }
            if (_service?.State.IsAttached != true) _service = null;
        }
        finally { _transition = false; SetButtons(); }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_transition || _service == null) return;
        try
        {
            AddRows(_service.Poll()); UpdateStatistics(); ShowSelected();
            if (!_service.State.IsRunning)
            {
                _timer.Stop();
                StatusLabel.Text = string.IsNullOrWhiteSpace(_service.LastError) ? "目标追踪已结束；可停止解除附加并保留结果。" : _service.LastError;
                SetButtons();
            }
        }
        catch (Exception ex)
        {
            _timer.Stop(); StatusLabel.Text = "捕获未完成：" + ex.Message;
        }
    }

    private void AddRows(IReadOnlyList<AccessTraceHit> added)
    {
        foreach (var hit in added) _rows.Add(hit);
        if (HitGrid.SelectedItem == null && _rows.Count > 0) HitGrid.SelectedIndex = 0;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private Task StopAsync()
    {
        if (_stopTask is { IsCompleted: false }) return _stopTask;
        return _stopTask = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _timer.Stop();
        if (_service == null) return;
        _transition = true; SetButtons();
        StatusLabel.Text = "正在恢复页面保护并解除调试附加…";
        try
        {
            await Task.Run(_service.Stop);
            // Stop drains queued events; rebuild membership without dropping prior rows.
            foreach (var hit in _service.Hits) if (!_rows.Contains(hit)) _rows.Add(hit);
            UpdateStatistics(); ShowSelected();
            StatusLabel.Text = "已停止，页面保护已恢复并解除附加；捕获结果已保留。";
        }
        catch (Exception ex)
        {
            if (_service.State.IsAttached)
                StatusLabel.Text = "停止尚未完成；窗口将保留以便重试：" + ex.Message;
            else
                StatusLabel.Text = "已解除附加；追踪结束时报告错误：" + ex.Message;
            foreach (var hit in _service.Hits) if (!_rows.Contains(hit)) _rows.Add(hit);
            UpdateStatistics(); ShowSelected();
        }
        finally { _transition = false; SetButtons(); }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        await CloseSafelyAsync();
    }

    /// <summary>Owners must call this before closing themselves: WPF skips owned Closing events.</summary>
    public Task<bool> CloseSafelyAsync()
    {
        if (_allowClose) return Task.FromResult(true);
        if (_closeTask is { IsCompleted: false }) return _closeTask;
        return _closeTask = CloseCoreAsync();
    }

    private async Task<bool> CloseCoreAsync()
    {
        _closing = true; SetButtons();
        // Close can also be requested before Start. Never call Close reentrantly inside Closing.
        await Task.Yield();
        try
        {
            // If attachment is still in progress, give it time to settle before detaching.
            while (_transition && (_stopTask == null || _stopTask.IsCompleted)) await Task.Delay(20);
            await StopAsync();
            if (_service?.State.IsAttached == true)
            {
                _closing = false;
                StatusLabel.Text = "目标仍处于调试附加状态；请重试停止，解除附加后才能关闭。";
                SetButtons();
                return false;
            }
            _service?.Dispose();
            _timer.Stop();
            _allowClose = true;
            Close();
            return true;
        }
        catch (Exception ex)
        {
            _closing = false;
            StatusLabel.Text = "关闭前的恢复未完成：" + ex.Message;
            SetButtons();
            return false;
        }
    }

    private void SetButtons()
    {
        bool attached = !_transition && _service?.State.IsAttached == true;
        StartButton.IsEnabled = !_transition && !_closing && !attached && _browsers.Count == 0;
        StartButton.ToolTip = _browsers.Count > 0 ? "请先关闭本次打开的浏览器，再重新开始追踪，以免浏览操作触碰监控页。" : null;
        StopButton.IsEnabled = !_transition && !_closing && attached;
        ClearButton.IsEnabled = !_transition && !_closing && !attached;
        BrowseButton.IsEnabled = !_transition && !_closing && !_browsing && HitGrid.SelectedItem is AccessTraceHit;
    }

    private void UpdateStatistics()
    {
        if (_service == null) { StatisticsLabel.Text = "尚未开始"; return; }
        var state = _service.State;
        StatisticsLabel.Text = $"{_rows.Count:N0} 条来源 · {_service.AcceptedEvents:N0} 次捕获 · 排除 {_service.FilteredEvents + _service.UnresolvedEvents:N0} 个候选";
        if (state.DroppedEvents > 0 || _service.SuppressedEvents > 0)
            StatisticsLabel.Text += $" · 丢弃 {state.DroppedEvents + _service.SuppressedEvents:N0}";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transition || _service?.State.IsAttached == true) return;
        try { _service?.Dispose(); } catch { }
        _service = null; _rows.Clear(); RegistersText.Clear();
        DetailLabel.Text = "选择一条指令，查看最后一次触发时执行前的寄存器快照。";
        StatisticsLabel.Text = "结果已清空";
    }

    private void HitGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { ShowSelected(); SetButtons(); }

    public async Task BrowseSelectedAsync()
    {
        if (_transition || _closing || _browsing || HitGrid.SelectedItem is not AccessTraceHit hit) return;
        _browsing = true; SetButtons();
        try
        {
            // Preserve the captured IP/registers. Browsers display live memory only after detach.
            await StopAsync();
            if (_closing || _service?.State.IsAttached == true)
            {
                if (!_closing) StatusLabel.Text = "调试附加尚未解除；请重试停止，解除后才能打开浏览器。";
                return;
            }
            ulong dataAddress = hit.Latest.MemoryAddress;
            var codeEngine = new NativeEngine(_processId);
            DisassemblyWindow code;
            try { code = new DisassemblyWindow(codeEngine, _processId, hit.InstructionPointer, dataAddress, ownsEngine: true) { Owner = this }; }
            catch { codeEngine.Dispose(); throw; }
            TrackBrowser(code);
            try { code.Show(); } catch { _browsers.Remove(code); codeEngine.Dispose(); throw; }
            var dataEngine = new NativeEngine(_processId);
            HexViewerWindow data;
            try { data = new HexViewerWindow(dataEngine, dataAddress, _processId, ownsEngine: true) { Owner = this }; }
            catch { dataEngine.Dispose(); throw; }
            TrackBrowser(data);
            try { data.Show(); } catch { _browsers.Remove(data); dataEngine.Dispose(); throw; }
            code.Activate();
            StatusLabel.Text = "已解除附加并保留捕获结果。浏览器显示停止后的实时代码/数据；寄存器仍是所选来源的最后一次捕获快照。";
        }
        catch (Exception ex) { StatusLabel.Text = "无法打开来源浏览器：" + ex.Message; }
        finally { _browsing = false; SetButtons(); }
    }
    private void TrackBrowser(Window window)
    {
        _browsers.Add(window);
        window.Closed += (_, _) => { _browsers.Remove(window); if (!_allowClose) SetButtons(); };
    }
    private async void BrowseButton_Click(object sender, RoutedEventArgs e) => await BrowseSelectedAsync();
    private async void HitGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(HitGrid, e.OriginalSource as DependencyObject) is not DataGridRow) return;
        e.Handled = true; await BrowseSelectedAsync();
    }
    private async void HitGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await BrowseSelectedAsync(); } }

    private void ShowSelected()
    {
        if (HitGrid.SelectedItem is not AccessTraceHit hit) return;
        var value = hit.Latest;
        DetailLabel.Text = $"最近触发 · 0x{value.FaultAddress:X16} · 操作数 {hit.MemoryRangeText} · {value.Bitness} 位 · " +
            (value.AddressFromRegisters ? "已由寄存器校验地址" : "地址由异常提供");
        string[] names = value.Bitness == 64
            ? ["RAX", "RCX", "RDX", "RBX", "RSP", "RBP", "RSI", "RDI", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"]
            : ["EAX", "ECX", "EDX", "EBX", "ESP", "EBP", "ESI", "EDI"];
        var builder = new StringBuilder();
        for (int i = 0; i < names.Length; ++i)
        {
            ulong register = value.Bitness == 64 ? value.Registers[i] : (uint)value.Registers[i];
            builder.Append($"{names[i],-3} {register:X16}   ");
            if (i % 4 == 3) builder.AppendLine();
        }
        builder.Append($"IP  {value.InstructionPointer:X16}   FLAGS {value.Flags:X16}   SEQ {value.Sequence}");
        string text = builder.ToString();
        if (RegistersText.Text != text) RegistersText.Text = text;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (HitGrid.SelectedItem is not AccessTraceHit hit) { StatusLabel.Text = "请先选择一条指令。"; return; }
        try
        {
            Clipboard.SetText($"{hit.AddressText}  {hit.BytesText}  {hit.Instruction}\n{hit.AccessText} · TID {hit.ThreadId} · {hit.Count:N0} 次 · {hit.MemoryRangeText}");
            StatusLabel.Text = "已复制选中指令及访问范围。";
        }
        catch (Exception ex) { StatusLabel.Text = "复制未完成：" + ex.Message; }
    }
}
