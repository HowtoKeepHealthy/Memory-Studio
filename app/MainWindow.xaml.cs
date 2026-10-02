using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace MemoryStudio;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _pickerTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private WindowProcessPicker.Highlight? _pickerHighlight;
    private WindowTarget? _pickerTarget;
    private Cursor? _previousCursor;
    private bool _picking;
    private ContextMenu? _recordContextMenu;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        _pickerTimer.Tick += (_, _) => UpdatePickerTarget();
        Closed += (_, _) => { EndPickerCapture(); if (_recordContextMenu is not null) _recordContextMenu.IsOpen = false; };
    }

    private void PickerButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_picking || DataContext is not MainViewModel vm || vm.IsBusy) return;
        e.Handled = true;
        _picking = true;
        _previousCursor = Mouse.OverrideCursor;
        if (!Mouse.Capture(PickerButton, CaptureMode.Element))
        {
            _picking = false;
            vm.SetStatus("无法捕获鼠标，请重新拖动准星。");
            return;
        }
        PickerButton.Focus();
        Mouse.OverrideCursor = Cursors.Cross;
        _pickerHighlight = new WindowProcessPicker.Highlight();
        _pickerTimer.Start();
        UpdatePickerTarget();
    }

    private void UpdatePickerTarget()
    {
        if (!_picking || DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) { EndPickerCapture(); vm.SetStatus("正在扫描，窗口选择已取消。"); return; }
        nint hwnd = WindowProcessPicker.WindowAtCursor();
        // The highlighter has a hollow native region; its narrow border must not replace the target beneath it.
        if (_pickerHighlight?.Handle == hwnd && _pickerTarget is not null) return;
        _pickerTarget = WindowProcessPicker.ResolveWindow(hwnd);
        if (_pickerTarget is null)
        {
            _pickerHighlight?.Hide();
            vm.SetStatus("将准星拖到目标窗口，释放即可连接（Esc 取消）。");
        }
        else
        {
            _pickerHighlight?.ShowTarget(_pickerTarget);
            string title = string.IsNullOrWhiteSpace(_pickerTarget.WindowTitle) ? "未命名窗口" : _pickerTarget.WindowTitle;
            vm.SetStatus($"释放连接：{_pickerTarget.DisplayName} · {title}");
        }
    }

    private async void PickerButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        e.Handled = true;
        EndPickerCapture();
        if (DataContext is not MainViewModel vm) return;
        if (vm.IsBusy) { vm.SetStatus("正在扫描，窗口选择已取消。"); return; }
        // Resolve again after removing the highlight, so selection uses the real HWND/PID at release.
        WindowTarget? target = WindowProcessPicker.ResolveCursor();
        if (target is null) { vm.SetStatus("未选择有效目标窗口。按住准星并拖到其他程序窗口后释放。"); return; }
        try { await vm.AttachToProcessAsync(target.ProcessId); }
        catch (Exception ex) { vm.SetStatus($"连接窗口进程未完成：{ex.Message}"); }
    }

    private void PickerButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_picking) return;
        EndPickerCapture();
        if (DataContext is MainViewModel vm) vm.SetStatus("窗口选择已取消。");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_picking && e.Key == Key.Escape)
        {
            e.Handled = true;
            EndPickerCapture();
            if (DataContext is MainViewModel vm) vm.SetStatus("窗口选择已取消。");
        }
        base.OnPreviewKeyDown(e);
    }

    private void EndPickerCapture()
    {
        if (!_picking && _pickerHighlight is null) return;
        _picking = false;
        _pickerTimer.Stop();
        _pickerHighlight?.Dispose();
        _pickerHighlight = null;
        _pickerTarget = null;
        Mouse.OverrideCursor = _previousCursor;
        _previousCursor = null;
        if (Mouse.Captured == PickerButton) Mouse.Capture(null);
    }

    private void ScanNavigation_Click(object sender, RoutedEventArgs e) => NavigateTo(0);
    private void WatchNavigation_Click(object sender, RoutedEventArgs e) => NavigateTo(1);
    private void AboutNavigation_Click(object sender, RoutedEventArgs e) => NavigateTo(2);

    private void NavigateTo(int index)
    {
        WorkspaceTabs.SelectedIndex = index;
        ScanNavigation.IsChecked = index == 0;
        WatchNavigation.IsChecked = index == 1;
        AboutNavigation.IsChecked = index == 2;
    }

    private async void RecordGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not MainViewModel vm) return;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        var row = FindAncestor<DataGridRow>(cell);
        if (cell is null || row?.Item is not (ResultRow or WatchRow) || cell.Column.Header?.ToString() == "冻结") return;
        string? action = ColumnEditAction(cell.Column);
        if (action is null) return;
        e.Handled = true;
        grid.SelectedItem = row.Item;
        await DispatchRecordActionAsync(vm, row.Item, action);
    }

    private async void RecordGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not DataGrid grid || DataContext is not MainViewModel vm) return;
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        var row = FindAncestor<DataGridRow>(cell);
        if (cell?.Column.Header?.ToString() != "冻结" || row?.Item is not WatchRow) return;
        // A checkbox already toggled on the first click. Suppress the second toggle and edit the frozen value instead.
        e.Handled = true;
        grid.SelectedItem = row.Item;
        await DispatchRecordActionAsync(vm, row.Item, "edit-value");
    }

    private void RecordGrid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not MainViewModel vm) return;
        e.Handled = true;
        if (_recordContextMenu is { } previousMenu)
        {
            _recordContextMenu = null;
            previousMenu.IsOpen = false;
        }
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not (ResultRow or WatchRow)) return;
        // Right-clicking a selected row keeps the entire group; an unselected row replaces it.
        if (!grid.SelectedItems.Contains(row.Item))
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = row.Item;
        }
        object[] records = grid.SelectedItems.Cast<object>().Where(item => item is ResultRow or WatchRow).ToArray();
        if (records.Length == 0) return;
        var menu = BuildRecordMenu(grid, records, vm.IsBusy);
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.MousePoint;
        _recordContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_recordContextMenu, menu)) _recordContextMenu = null; };
        menu.IsOpen = true;
    }

    private async void RecordGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is DataGrid grid && e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (grid == ResultGrid) await SelectAllScanRecordsAsync();
            else SelectAllRecords(grid);
        }
    }

    private void SelectAllResults_Click(object sender, RoutedEventArgs e) => SelectAllRecords(ResultGrid);
    private async void SelectAllScanResults_Click(object sender, RoutedEventArgs e) => await SelectAllScanRecordsAsync();
    private void SelectAllCompactWatch_Click(object sender, RoutedEventArgs e) => SelectAllRecords(CompactWatchGrid);
    private void SelectAllFullWatch_Click(object sender, RoutedEventArgs e) => SelectAllRecords(FullWatchGrid);

    private void SelectAllRecords(DataGrid grid)
    {
        grid.SelectAll();
        if (DataContext is MainViewModel vm)
            vm.SetStatus(grid == ResultGrid ? $"已全选本页 {grid.SelectedItems.Count:N0} 项结果。" : $"已全选 {grid.SelectedItems.Count:N0} 个监视地址。");
    }

    private async Task SelectAllScanRecordsAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            if (await vm.SelectAllScanResultsAsync())
            {
                ResultGrid.SelectAll();
                vm.SetStatus($"已全选全部 {ResultGrid.SelectedItems.Count:N0} 项扫描结果（跨页）。");
            }
        }
        catch (Exception ex) { vm.SetStatus($"全选扫描结果未完成：{ex.Message}"); }
    }

    private ContextMenu BuildRecordMenu(DataGrid grid, object[] records, bool busy)
    {
        // Snapshot records rather than retaining virtualized row containers or the changing selection.
        bool multiple = records.Length > 1;
        bool watches = records[0] is WatchRow;
        var menu = new ContextMenu
        {
            DataContext = records, Tag = grid, Style = (Style)FindResource("RecordContextMenu"),
            MaxHeight = Math.Max(240, Math.Min(640, SystemParameters.WorkArea.Height - 32))
        };
        menu.Items.Add(new MenuItem { Header = $"已选择 {records.Length:N0} 项", IsEnabled = false, Style = (Style)FindResource("RecordMenuTitle") });
        AddMenuItem(menu, grid == ResultGrid ? "全选本页" : "全选全部", "select-all", false, grid == ResultGrid ? "" : "Ctrl+A");
        if (grid == ResultGrid) AddMenuItem(menu, "全选全部扫描结果（跨页）", "select-all-scan", busy, "Ctrl+A");
        AddMenuSeparator(menu);
        AddMenuItem(menu, multiple ? "批量编辑数值…" : "编辑数值…", "edit-value", busy, multiple ? "" : "双击数值");
        SetSingleRecordOnly(AddMenuItem(menu, "编辑地址…", "edit-address", busy, multiple ? "仅单条" : "双击地址"), multiple);
        AddMenuItem(menu, multiple ? "批量更改数据类型…" : "更改数据类型…", "edit-type", busy, multiple ? "" : "双击类型");
        if (watches) AddMenuItem(menu, multiple ? "批量编辑描述…" : "编辑描述…", "edit-description", busy);
        AddMenuSeparator(menu);
        if (!watches)
            AddMenuItem(menu, multiple ? $"添加所选结果到地址表（{records.Length:N0} 项）" : "添加到地址表", "add-watch", busy);
        else
        {
            if (multiple)
            {
                AddMenuItem(menu, "冻结所选地址", "freeze", busy);
                AddMenuItem(menu, "解除所选冻结", "unfreeze", busy);
            }
            else
            {
                var freeze = AddMenuItem(menu, "冻结此地址", "toggle-freeze", busy);
                freeze.IsCheckable = true;
                freeze.IsChecked = ((WatchRow)records[0]).IsFrozen;
            }
            AddMenuItem(menu, multiple ? $"批量移除地址（{records.Length:N0} 项）" : "从地址表移除", "remove-watch", busy);
        }
        AddMenuSeparator(menu);
        SetSingleRecordOnly(AddMenuItem(menu, "内存查看器", "hex", busy), multiple);
        SetSingleRecordOnly(AddMenuItem(menu, "反汇编", "disassemble", busy), multiple);
        AddMenuSeparator(menu);
        SetSingleRecordOnly(AddMenuItem(menu, "查找写入来源…", "trace-write", busy), multiple, "定位执行写入此地址的指令，包括会写回结果的读改写指令。");
        SetSingleRecordOnly(AddMenuItem(menu, "查找访问来源（读取/写入）…", "trace-access", busy), multiple, "捕获读取和写入，并按指令标记实际访问类型；读改写指令也会出现。");
        AddMenuSeparator(menu);
        AddMenuItem(menu, multiple ? "复制所选地址" : "复制地址", "copy-address", busy);
        AddMenuItem(menu, multiple ? "复制所选数值" : "复制数值", "copy-value", busy);
        AddMenuItem(menu, multiple ? "复制所选完整记录" : "复制完整记录", "copy-record", busy);
        return menu;
    }

    private static void SetSingleRecordOnly(MenuItem item, bool multiple, string? tooltip = null)
    {
        item.ToolTip = multiple ? $"仅选中一条记录时可用。{tooltip}" : tooltip;
        if (!multiple) return;
        item.IsEnabled = false;
        item.InputGestureText = "仅单条";
        ToolTipService.SetShowOnDisabled(item, true);
    }

    private MenuItem AddMenuItem(ContextMenu menu, string label, string action, bool busy, string gesture = "")
    {
        var item = new MenuItem
        {
            Header = label, Tag = action, InputGestureText = gesture,
            Style = (Style)FindResource("RecordMenuItem"),
            IsEnabled = !busy || action.StartsWith("copy-", StringComparison.Ordinal)
        };
        item.Click += RecordMenuItem_Click;
        menu.Items.Add(item);
        return item;
    }

    private void AddMenuSeparator(ContextMenu menu) => menu.Items.Add(new Separator { Style = (Style)FindResource("RecordMenuSeparator") });

    private async void RecordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string action || DataContext is not MainViewModel vm) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu || menu.DataContext is not object[] records) return;
        e.Handled = true;
        if (action == "select-all")
        {
            if (menu.Tag is DataGrid grid) SelectAllRecords(grid);
            return;
        }
        if (action == "select-all-scan") { await SelectAllScanRecordsAsync(); return; }
        try { await vm.HandleRecordsActionAsync(records, action); }
        catch (Exception ex) { vm.SetStatus($"批量地址操作未完成：{ex.Message}"); }
    }

    private static string? ColumnEditAction(DataGridColumn column)
    {
        if (column is not DataGridBoundColumn { Binding: System.Windows.Data.Binding binding }) return null;
        return binding.Path?.Path switch
        {
            "AddressText" => "edit-address",
            "ValueText" => "edit-value",
            "TypeLabel" => "edit-type",
            "Description" => "edit-description",
            _ => null
        };
    }

    private static async Task DispatchRecordActionAsync(MainViewModel vm, object record, string action)
    {
        try { await vm.HandleRecordActionAsync(record, action); }
        catch (Exception ex) { vm.SetStatus($"地址操作未完成：{ex.Message}"); }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null;)
        {
            if (current is T ancestor) return ancestor;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}

internal static class WindowAppearance
{
    internal static void ApplyDarkTitleBar(Window window)
    {
        try { window.Icon ??= new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute)); }
        catch (System.IO.IOException) { }
        catch (NotSupportedException) { }
        try
        {
            int enabled = 1;
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
