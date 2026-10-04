using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;

namespace MemoryStudio;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _pickerTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private WindowProcessPicker.Highlight? _pickerHighlight;
    private WindowTarget? _pickerTarget;
    private Cursor? _previousCursor;
    private bool _picking;
    private ContextMenu? _recordContextMenu;
    private bool? _compactLayout;
    private bool _settingsExpanded = true;
    private bool _layoutUpdating;
    private bool _visibleResultsQueued;
    private bool _closingTraces;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        _pickerTimer.Tick += (_, _) => UpdatePickerTarget();
        PreviewMouseLeftButtonDown += MainWindow_PreviewMouseLeftButtonDown;
        Loaded += (_, _) => { UpdateResponsiveLayout(); QueueVisibleResults(); };
        Closing += MainWindow_Closing;
        AppearanceSettings.Changed += AppearanceSettings_Changed;
        Closed += (_, _) => { EndPickerCapture(); AppearanceSettings.Changed -= AppearanceSettings_Changed; if (_recordContextMenu is not null) _recordContextMenu.IsOpen = false; };
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || DataContext is not MainViewModel vm) return;
        e.Cancel = true;
        if (_closingTraces) return;
        _closingTraces = true;
        try
        {
            if (await vm.CloseTraceWindowsAsync())
            {
                _allowClose = true;
                // A trace-free close may complete synchronously, while WPF is still inside its Closing event.
                _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Normal);
            }
        }
        catch (Exception ex) { vm.SetStatus($"退出前停止访问追踪未完成：{ex.Message}"); }
        finally { _closingTraces = false; }
    }

    private void AppearanceSettings_Changed(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateResponsiveLayout, DispatcherPriority.Loaded);
    private void MainShell_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        if (_layoutUpdating || MainShell is null || MainShell.ActualWidth <= 0) return;
        _layoutUpdating = true;
        try
        {
            double width = MainShell.ActualWidth;
            bool compact = width < 1100;
            if (_compactLayout != compact) { _compactLayout = compact; _settingsExpanded = true; }
            bool iconNavigation = width < 600 || MainShell.ActualHeight < 410;
            RailColumn.Width = new GridLength(compact ? iconNavigation ? 52 : AppearanceSettings.Current.FontSize > 16 ? 76 : 64 : 82);
            NavScanLabel.Visibility = NavWatchLabel.Visibility = NavAboutLabel.Visibility = iconNavigation ? Visibility.Collapsed : Visibility.Visible;
            MainHeaderRow.Height = GridLength.Auto;
            MainHeaderRow.MinHeight = compact ? 100 : 86;
            RailBrandRow.Height = new GridLength(Math.Max(MainHeaderRow.MinHeight, MainHeaderRow.ActualHeight));
            HeaderLayout.Margin = new Thickness(compact ? 12 : 20, compact ? 8 : 12, compact ? 12 : 20, compact ? 8 : 12);
            HeaderFirstRow.Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            HeaderSecondRow.Height = compact ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(HeaderActions, compact ? 0 : 1);
            Grid.SetRow(HeaderActions, compact ? 1 : 0);
            Grid.SetColumnSpan(HeaderActions, compact ? 2 : 1);
            HeaderActions.Margin = new Thickness(0, compact ? 8 : 0, 0, 0);
            Grid.SetColumnSpan(BrandHeader, compact ? 2 : 1);
            BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ProcessCombo.Width = compact ? width < 600 ? 125 : 185 : 225;
            ProcessBadgeText.MaxWidth = compact ? width < 600 ? 45 : 130 : 280;
            PracticeButton.Content = width < 600 ? "练习 ↗" : "启动练习 ↗";
            WorkspaceTabs.Margin = compact ? new Thickness(12, 12, 12, 8) : new Thickness(16, 14, 16, 10);
            ScanSettingsColumn.Width = new GridLength(compact ? 230 : 260);
            ScanGapColumn.Width = new GridLength(14);
            // Keep search beside the results at every display scale. A bounded page width preserves grid virtualization.
            double pageWidth = Math.Max(700, width - RailColumn.Width.Value - WorkspaceTabs.Margin.Left - WorkspaceTabs.Margin.Right - 12);
            ScanPageLayout.Width = pageWidth;
            double bodyHeight = Math.Max(150, MainShell.ActualHeight - MainHeaderRow.ActualHeight - 58);
            CompactWatchCard.Height = Math.Max(Math.Max(200, 200 * AppearanceSettings.Current.FontSize / 13), (bodyHeight - 60) * 0.4);
            ResultsCard.Height = Math.Max(300, bodyHeight - CompactWatchCard.Height - 60);
            ScanSettingsFields.Visibility = _settingsExpanded ? Visibility.Visible : Visibility.Collapsed;
            ScanSettingsCard.Height = _settingsExpanded ? ResultsCard.Height : double.NaN;
            ScanSettingsToggle.Content = _settingsExpanded ? "扫描设置  ▴" : "扫描设置  ▾";
            FullWatchCard.Height = Math.Max(220, bodyHeight - 165);
            QueueVisibleResults();
        }
        finally { _layoutUpdating = false; }
    }

    private void ScanSettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settingsExpanded = !_settingsExpanded;
        UpdateResponsiveLayout();
    }

    private void ResultGrid_Loaded(object sender, RoutedEventArgs e) => QueueVisibleResults();
    private void ResultGrid_ScrollChanged(object sender, ScrollChangedEventArgs e) => QueueVisibleResults();

    private void QueueVisibleResults()
    {
        if (_visibleResultsQueued || ResultGrid is null) return;
        _visibleResultsQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _visibleResultsQueued = false;
            if (DataContext is not MainViewModel vm) return;
            var bounds = new Rect(0, 0, ResultGrid.ActualWidth, ResultGrid.ActualHeight);
            var rows = new List<ResultRow>();
            foreach (var row in VisualDescendants<DataGridRow>(ResultGrid))
            {
                if (!row.IsVisible || row.ActualHeight <= 0 || row.Item is not ResultRow record) continue;
                try
                {
                    var rectangle = row.TransformToAncestor(ResultGrid).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                    var pageRectangle = row.TransformToAncestor(ScanPageScroll).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                    if (rectangle.IntersectsWith(bounds) && pageRectangle.IntersectsWith(new Rect(0, 0, ScanPageScroll.ActualWidth, ScanPageScroll.ActualHeight))) rows.Add(record);
                }
                catch (InvalidOperationException) { }
            }
            vm.SetVisibleResults(rows);
        }, DispatcherPriority.Background);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private void ToolsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainViewModel vm) return;
        var menu = new ContextMenu { Style = (Style)FindResource("RecordContextMenu"), PlacementTarget = button, Placement = PlacementMode.Bottom };
        void Add(string label, string command)
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("RecordMenuItem") };
            item.SetBinding(MenuItem.CommandProperty, new Binding(command) { Source = vm });
            menu.Items.Add(item);
        }
        Add("手动添加地址…", nameof(MainViewModel.AddManualAddressCommand));
        var undo = new MenuItem { Header = "撤销所选记录", Style = (Style)FindResource("RecordMenuItem"), IsEnabled = !vm.IsBusy && vm.CanUndoRecords(ActiveRecordSelection()) };
        undo.Click += UndoSelectedRecords_Click;
        menu.Items.Add(undo);
        Add("撤销上次扫描", nameof(MainViewModel.UndoScanCommand));
        Add(vm.PauseButtonText, nameof(MainViewModel.TogglePauseCommand));
        AddMenuSeparator(menu);
        Add("内存查看器", nameof(MainViewModel.OpenMemoryViewerCommand));
        Add("反汇编", nameof(MainViewModel.OpenDisassemblyCommand));
        Add("指针扫描…", nameof(MainViewModel.OpenPointerScannerCommand));
        Add("结构查看…", nameof(MainViewModel.OpenStructureViewerCommand));
        AddMenuSeparator(menu);
        Add("导入 CE 数据地址表 (.CT)…", nameof(MainViewModel.ImportCeTableCommand));
        Add("导出 CE 数据地址表 (.CT)…", nameof(MainViewModel.ExportCeTableCommand));
        AddMenuSeparator(menu);
        var appearance = new MenuItem { Header = "显示与窗口设置…", Style = (Style)FindResource("RecordMenuItem") };
        appearance.Click += (_, _) => AppearanceSettings.ShowSettings(this);
        menu.Items.Add(appearance);
        AppearanceSettings.ApplyPopup(menu);
        menu.IsOpen = true;
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

    private void MainWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_picking || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source) return;
        var grid = FindAncestor<DataGrid>(source);
        if (grid is not null)
        {
            if (FindAncestor<DataGridRow>(source)?.Item is ResultRow or WatchRow || HasInteractiveAncestor(source, grid)) return;
            grid.UnselectAll();
            return;
        }
        // Inputs, operation buttons, headers, and scrollbars must leave the group available for a subsequent action.
        if (HasInteractiveAncestor(source, this)) return;
        ResultGrid.UnselectAll();
        CompactWatchGrid.UnselectAll();
        FullWatchGrid.UnselectAll();
    }

    private static bool HasInteractiveAncestor(DependencyObject source, DependencyObject boundary)
    {
        for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, boundary); current = ParentOf(current))
            if (current is ButtonBase or TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or Slider or ScrollBar or Thumb or MenuItem or System.Windows.Controls.ContextMenu or DataGridColumnHeader or DataGridRowHeader or System.Windows.Documents.Hyperlink)
                return true;
        return false;
    }

    private async void RecordGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not MainViewModel vm) return;
        var source = e.OriginalSource as DependencyObject;
        var cell = FindAncestor<DataGridCell>(source);
        var row = FindAncestor<DataGridRow>(source);
        if (row?.Item is not (ResultRow or WatchRow)) return;
        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            bool currentFocus = ReferenceEquals(grid.CurrentCell.Item, row.Item);
            if (cell is not null && cell.Column.Header?.ToString() != "冻结" && currentFocus && grid.SelectedItems.Count > 1 && grid.SelectedItems.Contains(row.Item))
            {
                e.Handled = true;
                grid.Focus();
                return;
            }
            if (!currentFocus || !grid.SelectedItems.Contains(row.Item))
            {
                grid.SelectedItems.Clear();
                grid.SelectedItem = row.Item;
                var column = cell?.Column ?? grid.CurrentCell.Column ?? grid.Columns.FirstOrDefault();
                if (column is not null) grid.CurrentCell = new DataGridCellInfo(row.Item, column);
            }
        }
        if (cell is null) return;
        bool freeze = cell.Column.Header?.ToString() == "冻结";
        string? action = freeze ? "edit-value" : ColumnEditAction(cell.Column);
        if (action is null) return;
        if (e.ClickCount != 2 || Keyboard.Modifiers != ModifierKeys.None) return;
        // Freeze checkboxes toggle once on the first click. The second click edits the selected group's value.
        e.Handled = true;
        if (!grid.SelectedItems.Contains(row.Item)) { grid.SelectedItems.Clear(); grid.SelectedItem = row.Item; }
        object[] records = SelectedRecords(grid);
        if (records.Length > 1 && action == "edit-address") { vm.SetStatus("编辑地址需要只选中一条记录；数值、类型和描述支持批量编辑。"); return; }
        await DispatchRecordsActionAsync(vm, records, action);
    }

    private static object[] SelectedRecords(DataGrid grid) => grid.SelectedItems.Cast<object>().Where(item => item is ResultRow or WatchRow).ToArray();
    private async void AddSelectedResults_Click(object sender, RoutedEventArgs e) => await RunToolbarActionAsync(ResultGrid, "add-watch");
    private async void RemoveCompactSelected_Click(object sender, RoutedEventArgs e) => await RunToolbarActionAsync(CompactWatchGrid, "remove-watch");
    private async void RemoveFullSelected_Click(object sender, RoutedEventArgs e) => await RunToolbarActionAsync(FullWatchGrid, "remove-watch");
    private async void WriteCompactSelected_Click(object sender, RoutedEventArgs e) => await WriteToolbarSelectionAsync(CompactWatchGrid);
    private async void WriteFullSelected_Click(object sender, RoutedEventArgs e) => await WriteToolbarSelectionAsync(FullWatchGrid);
    private async Task RunToolbarActionAsync(DataGrid grid, string action)
    {
        if (DataContext is MainViewModel vm) await DispatchRecordsActionAsync(vm, SelectedRecords(grid), action);
    }
    private async Task WriteToolbarSelectionAsync(DataGrid grid)
    {
        if (DataContext is not MainViewModel vm) return;
        object[] records = SelectedRecords(grid);
        if (records.Length == 0) { vm.SetStatus("请先在地址表中选择要写入的记录。"); return; }
        try { await vm.WriteRecordsValueAsync(records, vm.EditValueText); }
        catch (Exception ex) { vm.SetStatus($"批量写入未完成：{ex.Message}"); }
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
        var column = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject)?.Column ?? grid.CurrentCell.Column ?? grid.Columns.FirstOrDefault();
        if (column is not null) grid.CurrentCell = new DataGridCellInfo(row.Item, column);
        object[] records = grid.SelectedItems.Cast<object>().Where(item => item is ResultRow or WatchRow).ToArray();
        if (records.Length == 0) return;
        var menu = BuildRecordMenu(grid, records, vm.IsBusy);
        menu.PlacementTarget = row;
        menu.Placement = PlacementMode.MousePoint;
        _recordContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_recordContextMenu, menu)) _recordContextMenu = null; };
        AppearanceSettings.ApplyPopup(menu);
        menu.IsOpen = true;
    }

    private async void RecordGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is DataGrid undoGrid && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await UndoSelectedRecordsAsync(undoGrid);
            return;
        }
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

    private object[] ActiveRecordSelection()
    {
        DataGrid grid = WorkspaceTabs.SelectedIndex == 1 ? FullWatchGrid :
            ResultGrid.IsKeyboardFocusWithin ? ResultGrid : CompactWatchGrid.SelectedItems.Count > 0 ? CompactWatchGrid : ResultGrid;
        return grid.SelectedItems.Cast<object>().Where(row => row is ResultRow or WatchRow).ToArray();
    }
    private async void UndoSelectedRecords_Click(object sender, RoutedEventArgs e)
    {
        DataGrid? grid = sender is DependencyObject source ? FindAncestor<Border>(source)?.Name switch
        { "CompactWatchCard" => CompactWatchGrid, "FullWatchCard" => FullWatchGrid, _ => null } : null;
        if (DataContext is MainViewModel vm)
            try { await vm.UndoRecordsAsync(grid?.SelectedItems.Cast<object>().ToArray() ?? ActiveRecordSelection()); }
            catch (Exception ex) { vm.SetStatus("撤销所选记录未完成：" + ex.Message); }
    }
    private async Task UndoSelectedRecordsAsync(DataGrid grid)
    {
        if (DataContext is not MainViewModel vm) return;
        try { await vm.UndoRecordsAsync(grid.SelectedItems.Cast<object>().Where(row => row is ResultRow or WatchRow).ToArray()); }
        catch (Exception ex) { vm.SetStatus("撤销所选记录未完成：" + ex.Message); }
    }

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
        var undoRecord = AddMenuItem(menu, multiple ? "各自撤销所选记录的最近一次编辑" : "撤销此记录的最近一次编辑", "undo-record", busy, "Ctrl+Z");
        undoRecord.IsEnabled = !busy && DataContext is MainViewModel vm && vm.CanUndoRecords(records);
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
        SetSingleRecordOnly(AddMenuItem(menu, "分析地址 / 导出 AI 上下文…", "analyze", busy), multiple, "汇集数据、模块、附近指令及已捕获的读写证据，可离线查看或导出供 AI 分析。");
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

    private static async Task DispatchRecordsActionAsync(MainViewModel vm, object[] records, string action)
    {
        if (records.Length == 0) { vm.SetStatus("请先选择记录。"); return; }
        try { await vm.HandleRecordsActionAsync(records, action); }
        catch (Exception ex) { vm.SetStatus($"地址操作未完成：{ex.Message}"); }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null;)
        {
            if (current is T ancestor) return ancestor;
            current = ParentOf(current);
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject source) => source is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
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
