using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using MemoryStudio;

internal static class Program
{
    private static readonly List<string> Report = [];
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MainWindow _window = null!;
    private static MainViewModel _vm = null!;
    private static RecordEditRequest? _lastEditorRequest;
    private static nint _fixtureMemory;
    private static string _outputDirectory = AppContext.BaseDirectory;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) _outputDirectory = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(_outputDirectory);
        int exit = 0;
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/WpfInterfaceCheck;component/Theme.xaml") });
            AppearanceSettings.TransientSession = true;
            AppearanceSettings.SuspendSizeMemory = true;
            ThemeManager.Apply("dark");
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is RecordEditorWindow editor)
                {
                    _lastEditorRequest = (RecordEditRequest)typeof(RecordEditorWindow).GetField("_request", PrivateInstance)!.GetValue(editor)!;
                    editor.ShowInTaskbar = false;
                    editor.Close();
                }
            }));
            _vm = new MainViewModel();
            for (int i = 0; i < 3; i++)
            {
                byte[] bytes = BitConverter.GetBytes(100 + i);
                _vm.Results.Add(new ResultRow((ulong)(0x1000 + i * 16), bytes, (100 + i).ToString(), ValueCodec.TypeLabel(2), 2, 4));
                _vm.Watches.Add(new WatchRow { Address = (ulong)(0x2000 + i * 16), Type = 2, Size = 4, FrozenValue = bytes, ValueText = (100 + i).ToString(), Description = $"fixture-{i}" });
            }
            _window = new MainWindow { DataContext = _vm, Left = -15000, Top = -15000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            application.MainWindow = _window;
            // Keep test context menus zero-sized; no visible menu opens on the user's desktop.
            var menuStyle = new Style(typeof(ContextMenu), (Style)_window.FindResource("RecordContextMenu"));
            menuStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            _window.Resources["RecordContextMenu"] = menuStyle;
            _window.Show(); Pump(); _window.UpdateLayout();
            var resultGrid = (DataGrid)_window.FindName("ResultGrid");
            var compactGrid = (DataGrid)_window.FindName("CompactWatchGrid");
            var fullGrid = (DataGrid)_window.FindName("FullWatchGrid");
            CheckGrid(resultGrid, "results");
            CheckGrid(compactGrid, "compact watches");
            ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            Pump(); _window.UpdateLayout();
            CheckGrid(fullGrid, "full watches");
            CheckColumnMap(resultGrid);
            CheckColumnMap(fullGrid);
            CheckWatchCtrlA(fullGrid);
            typeof(MainViewModel).GetProperty("IsBusy")!.SetValue(_vm, true);
            var busyMenu = (ContextMenu)Invoke("BuildRecordMenu", resultGrid, new object[] { resultGrid.Items[0], resultGrid.Items[1] }, true)!;
            Require(MenuItem(busyMenu, "copy-address").IsEnabled && MenuItem(busyMenu, "copy-value").IsEnabled, "busy menu keeps copying enabled");
            Require(!MenuItem(busyMenu, "edit-value").IsEnabled && !MenuItem(busyMenu, "add-watch").IsEnabled, "busy menu disables modifying records");
            typeof(MainViewModel).GetProperty("IsBusy")!.SetValue(_vm, false);
            ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 0;
            Pump();
            Task crossPage = Dispatcher.CurrentDispatcher.Invoke(() => CheckCrossPageAsync(resultGrid));
            WaitUntil(() => crossPage.IsCompleted, 8000);
            crossPage.GetAwaiter().GetResult();
            Task unknown = Dispatcher.CurrentDispatcher.Invoke(CheckUnknownFlowAsync);
            WaitUntil(() => unknown.IsCompleted, 10000);
            unknown.GetAwaiter().GetResult();
            CheckNewInteraction(resultGrid, compactGrid, fullGrid);
            CheckLayouts();
        }
        catch (Exception ex)
        {
            Report.Add("FAIL: " + ex); exit = 1;
        }
        finally
        {
            CloseMenu();
            _window?.Close(); Pump(); _vm?.Dispose();
            if (_fixtureMemory != 0) VirtualFree(_fixtureMemory, 0, 0x8000);
            File.WriteAllLines(Path.Combine(_outputDirectory, "wpf-interface-results.txt"), Report);
            foreach (string line in Report) Console.WriteLine(line);
            Application.Current?.Shutdown();
        }
        return exit;
    }

    private static void CheckNewInteraction(DataGrid resultGrid, DataGrid compactGrid, DataGrid fullGrid)
    {
        foreach (var grid in new[] { resultGrid, compactGrid, fullGrid })
        {
            ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = grid == fullGrid ? 1 : 0;
            Pump(); _window.UpdateLayout();
            grid.SelectedItems.Clear(); grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]);
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[0]);
            grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns.OfType<DataGridBoundColumn>().First());
            foreach (var column in grid.Columns.OfType<DataGridBoundColumn>().Where(c => ((System.Windows.Data.Binding)c.Binding).Path.Path is "ValueText" or "TypeLabel" or "Description"))
            {
                var cell = Descendants<DataGridCell>(row).Single(c => c.Column == column);
                _lastEditorRequest = null;
                var first = LeftClick(grid, cell, 1);
                Require(first.Handled && grid.SelectedItems.Count == 2, grid.Name + " real preview click on the current focus row preserves its group" + $" [handled={first.Handled}, selected={grid.SelectedItems.Count}, focus={ReferenceEquals(grid.CurrentCell.Item, grid.Items[0])}]");
                LeftClick(grid, cell, 2); Pump();
                Require(grid.SelectedItems.Count == 2 && _lastEditorRequest?.AddressText.Contains("2") == true, grid.Name + " double click edits two actual selected records: " + column.Header);
            }
            var second = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[1]);
            var secondValue = Descendants<DataGridCell>(second).Single(c => c.Column is DataGridBoundColumn b && ((Binding)b.Binding).Path.Path == "ValueText");
            var another = LeftClick(grid, secondValue, 1);
            Require(!another.Handled && grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, grid.Items[1]), grid.Name + " ordinary click on another already selected row replaces the old group");
            grid.SelectedItems.Add(grid.Items[0]);
            grid.CurrentCell = new DataGridCellInfo(grid.Items[1], secondValue.Column);
            foreach (var modifier in new[] { ModifierKeys.Control, ModifierKeys.Shift })
            {
                var modified = WithModifiers(modifier, () => LeftClick(grid, secondValue, 1));
                Require(!modified.Handled && grid.SelectedItems.Count == 2, grid.Name + " custom preview handler leaves " + modifier + " selection to WPF");
            }
            LeftClick(grid, grid, 1);
            Require(grid.SelectedItems.Count == 0, grid.Name + " real preview click on table blank clears selection");
            grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]);
            var third = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[2]);
            var thirdValue = Descendants<DataGridCell>(third).Single(c => c.Column is DataGridBoundColumn b && ((System.Windows.Data.Binding)b.Binding).Path.Path == "ValueText");
            _lastEditorRequest = null;
            LeftClick(grid, thirdValue, 2); Pump();
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, grid.Items[2]), grid.Name + " double click on unselected record uses just that row");
        }
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 0; Pump();
        Require(Descendants<DataGridRow>(resultGrid).Count() < 50 && _vm.Results.Count == 1024, "bounded result card retains row virtualization inside page scroll");
        resultGrid.ScrollIntoView(resultGrid.Items[800]); Pump();
        var visible = (ResultRow[])typeof(MainViewModel).GetField("_visibleResults", PrivateInstance)!.GetValue(_vm)!;
        Require(visible.Length > 0 && visible.Length < 30 && visible.Any(r => ReferenceEquals(r, resultGrid.Items[800])), "visible-range callback follows result scrolling instead of all 1024 records");
        compactGrid.SelectedItems.Clear(); compactGrid.SelectedItems.Add(compactGrid.Items[0]); compactGrid.SelectedItems.Add(compactGrid.Items[1]);
        CheckOperationSelection(compactGrid);
        _vm.EditValueText = "444";
        Invoke("WriteCompactSelected_Click", _window, new RoutedEventArgs());
        WaitUntil(() => !_vm.IsBusy, 5000);
        Require(((WatchRow)compactGrid.Items[0]).ValueText == "444" && ((WatchRow)compactGrid.Items[1]).ValueText == "444" && Marshal.ReadInt32(_fixtureMemory) == 444 && Marshal.ReadInt32(_fixtureMemory, 4) == 444, "compact toolbar writes both selected watches into real memory");
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 1; Pump();
        fullGrid.SelectedItems.Clear(); fullGrid.SelectedItems.Add(fullGrid.Items[1]); fullGrid.SelectedItems.Add(fullGrid.Items[2]);
        CheckOperationSelection(fullGrid);
        _vm.EditValueText = "555";
        Invoke("WriteFullSelected_Click", _window, new RoutedEventArgs());
        WaitUntil(() => !_vm.IsBusy, 5000);
        Require(((WatchRow)fullGrid.Items[0]).ValueText == "444" && ((WatchRow)fullGrid.Items[1]).ValueText == "555" && ((WatchRow)fullGrid.Items[2]).ValueText == "555" && Marshal.ReadInt32(_fixtureMemory) == 444 && Marshal.ReadInt32(_fixtureMemory, 4) == 555 && Marshal.ReadInt32(_fixtureMemory, 8) == 555, "full toolbar writes its own selected group into real memory independently of compact grid selection");
        var undoMenu = (ContextMenu)Invoke("BuildRecordMenu", fullGrid, new object[] { fullGrid.Items[0] }, false)!;
        var undoItem = MenuItem(undoMenu, "undo-record");
        Require(undoItem.IsEnabled && MenuItem(undoMenu, "analyze").IsEnabled, "single record menu exposes independent undo and address analysis");
        undoItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        WaitUntil(() => !_vm.IsBusy && Marshal.ReadInt32(_fixtureMemory) == 100, 5000);
        Require(Marshal.ReadInt32(_fixtureMemory, 4) == 555 && Marshal.ReadInt32(_fixtureMemory, 8) == 555, "record menu undo restores only that record without undoing the earlier batch or other records");
        fullGrid.SelectedItems.Clear(); fullGrid.SelectedItems.Add(fullGrid.Items[1]); fullGrid.SelectedItems.Add(fullGrid.Items[2]);
        var fullUndo = Descendants<Button>((Border)_window.FindName("FullWatchCard")).Single(button => Equals(button.Content, "撤销所选"));
        fullUndo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => !_vm.IsBusy && Marshal.ReadInt32(_fixtureMemory, 4) == 444 && Marshal.ReadInt32(_fixtureMemory, 8) == 100, 5000);
        Require(Marshal.ReadInt32(_fixtureMemory) == 100, "toolbar batch undo rolls each selected record back one step and leaves the unselected record intact");
        resultGrid.SelectedItems.Add(resultGrid.Items[0]);
        LeftClick(fullGrid, (Grid)_window.FindName("MainShell"), 1);
        Require(resultGrid.SelectedItems.Count == 0 && compactGrid.SelectedItems.Count == 0 && fullGrid.SelectedItems.Count == 0, "real preview click on main content blank clears all record selections");
    }

    private static void CheckOperationSelection(DataGrid grid)
    {
        var input = Descendants<TextBox>(_window).First(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "EditValueText");
        LeftClick(grid, input, 1);
        Require(grid.SelectedItems.Count == 2, grid.Name + " value input click retains the selected group");
        var write = Descendants<Button>(_window).First(button => Equals(button.Content, "写入所选"));
        LeftClick(grid, write, 1);
        Require(grid.SelectedItems.Count == 2, grid.Name + " write operation button click retains the selected group");
    }

    private static async Task CheckUnknownFlowAsync()
    {
        _vm.SelectedType = _vm.TypeOptions.First(type => type.Value == 2);
        _vm.SelectedScanMode = _vm.ScanModes.First(mode => mode.Value == 1);
        _vm.SearchValue = "";
        Pump();
        Require(!((TextBox)_window.FindName("SearchValueInput")).IsEnabled, "unknown initial scan disables the actual value input instead of accepting a misleading value");
        string boundedStart = _vm.StartAddress, boundedEnd = _vm.EndAddress;
        _vm.StartAddress = "0x00000000"; _vm.EndAddress = "0x00007FFFFFFFFFFF";
        Require(_vm.FirstScanCommand.CanExecute(null) && !_vm.NextScanCommand.CanExecute(null), "unknown with empty value and the default full range enables first scan without value validation");
        _vm.StartAddress = boundedStart; _vm.EndAddress = boundedEnd;
        _vm.SelectedType = _vm.TypeOptions.First(type => type.Value == 4);
        _vm.FloatToleranceText = ""; _vm.RelativeToleranceText = "";
        await _vm.ScanAsync(false); Pump();
        Require(_vm.Results.Count == 200 && _vm.ResultSummary.Contains("1,024"), "unknown initial float scan ignores empty value and unused tolerance fields");
        Require(_vm.SelectedScanMode.Value == 2 && _vm.NextScanCommand.CanExecute(null) && !_vm.FirstScanCommand.CanExecute(null), "successful unknown switches to Changed with next-scan command enabled");
        Require(!((TextBox)_window.FindName("SearchValueInput")).IsEnabled, "changed-value scan does not request an unused input value");
        Marshal.Copy(BitConverter.GetBytes(0.5f), 0, _fixtureMemory + 2000, 4);
        _vm.FloatToleranceText = "0.001"; _vm.RelativeToleranceText = "0.000001";
        await _vm.ScanAsync(true); Pump();
        Require(_vm.Results.Count == 1 && _vm.Results[0].Address == (ulong)_fixtureMemory + 2000, "Changed narrows a real unknown snapshot to the modified float");
        ulong actualTotal = (ulong)typeof(MainViewModel).GetField("_total", PrivateInstance)!.GetValue(_vm)!;
        bool actualSnapshot = (bool)typeof(MainViewModel).GetField("_hasUnknownSnapshot", PrivateInstance)!.GetValue(_vm)!;
        try
        {
            typeof(MainViewModel).GetField("_total", PrivateInstance)!.SetValue(_vm, 2_000_001UL);
            typeof(MainViewModel).GetField("_hasUnknownSnapshot", PrivateInstance)!.SetValue(_vm, true);
            Require(!await _vm.SelectAllScanResultsAsync() && _vm.Results.Count == 1 && !_vm.IsBusy && _vm.ResultSummary.StartsWith("快照"), "snapshot stage rejects loading all candidates above two million while preserving the page");
        }
        finally
        {
            typeof(MainViewModel).GetField("_total", PrivateInstance)!.SetValue(_vm, actualTotal);
            typeof(MainViewModel).GetField("_hasUnknownSnapshot", PrivateInstance)!.SetValue(_vm, actualSnapshot);
        }
        Marshal.Copy(Enumerable.Repeat(100, 1024).ToArray(), 0, _fixtureMemory, 1024);
        _vm.SelectedType = _vm.TypeOptions.First(type => type.Value == 2);
        _vm.SelectedScanMode = _vm.ScanModes.First(mode => mode.Value == 0); _vm.SearchValue = "100";
        Pump();
        Require(((TextBox)_window.FindName("SearchValueInput")).IsEnabled, "returning to exact mode re-enables the existing search value input");
        await _vm.ScanAsync(false);
        if (!await _vm.SelectAllScanResultsAsync()) throw new InvalidOperationException("restore full result fixture failed");
        Pump();
    }

    private static void CheckLayouts()
    {
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 0;
        _window.Width = 1280; _window.Height = 820; Pump(); _window.UpdateLayout();
        double initialWatchHeight = ((Border)_window.FindName("CompactWatchCard")).ActualHeight;
        _window.Height = 1020; Pump(); _window.UpdateLayout();
        Require(((Border)_window.FindName("CompactWatchCard")).ActualHeight > initialWatchHeight + 50, "scan-page address table grows when the window becomes taller");
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 1; _window.Height = 820; Pump();
        double initialFullHeight = ((Border)_window.FindName("FullWatchCard")).ActualHeight;
        _window.Height = 1020; Pump();
        Require(((Border)_window.FindName("FullWatchCard")).ActualHeight > initialFullHeight + 100, "full address table fills the additional window height");
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 0; _window.Height = 820; Pump(); _window.UpdateLayout();
        Render("ui-1280.png");
        _window.Width = 780; _window.Height = 520; Pump(); _window.UpdateLayout();
        CheckSidebar("780x520");
        Require(((ScrollViewer)_window.FindName("ScanSettingsFields")).Visibility == Visibility.Visible, "compact layout keeps search conditions expanded beside the results");
        Require(((ScrollViewer)_window.FindName("ScanPageScroll")).ScrollableHeight > 0, "small-window page scroll exposes the address table and overflow content");
        Render("ui-780.png");
        var page = (ScrollViewer)_window.FindName("ScanPageScroll");
        Require(page.ScrollableWidth > 0, "small-window horizontal scroll reaches the bounded results without moving the sidebar above them");
        page.ScrollToRightEnd(); Pump(); CheckSidebar("780 horizontally scrolled");
        Render("ui-780-settings-expanded.png");
        page.ScrollToLeftEnd(); Pump();
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 1; Pump(); Render("ui-780-watches.png");
        ((TabControl)_window.FindName("WorkspaceTabs")).SelectedIndex = 0; Pump();
        AppearanceSettings.Initialize();
        double originalScale = AppearanceSettings.Current.ScalePercent, originalFont = AppearanceSettings.Current.FontSize;
        try
        {
            AppearanceSettings.Current.ScalePercent = 100; AppearanceSettings.Current.FontSize = 13;
            AppearanceSettings.Apply(_window); Pump();
            AppearanceSettings.Current.ScalePercent = 150; AppearanceSettings.Current.FontSize = 18; Pump(); _window.UpdateLayout();
            var root = (Grid)_window.FindName("MainShell");
            Require(root.LayoutTransform is ScaleTransform { ScaleX: 1.5, ScaleY: 1.5 }, "global display setting applies root layout scale");
            Require(Math.Abs(_window.FontSize - 18) < .01, "font setting applies once without inherited double multiplication");
            Require(((TextBlock)_window.FindName("NavWatchLabel")).Visibility == Visibility.Collapsed, "extreme zoom keeps navigation reachable through icon buttons and tooltips");
            CheckSidebar("780 at 150 percent with font 18");
            Render("ui-780-150-font18.png");
            var other = new Window { Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false, Left = -15000, Top = -15000, WindowStartupLocation = WindowStartupLocation.Manual, Content = new DockPanel { Children = { new TextBlock { Text = "字号检查" } } } };
            other.Show(); double otherBaseline = other.FontSize; AppearanceSettings.Apply(other); Pump();
            Require(((DockPanel)other.Content).LayoutTransform is ScaleTransform { ScaleX: 1.5 }, "global scale also covers code-based DockPanel tools");
            AppearanceSettings.Current.ScalePercent = 60; AppearanceSettings.Current.FontSize = 10; Pump();
            Require(((DockPanel)other.Content).LayoutTransform is ScaleTransform { ScaleX: .6 } && Math.Abs(other.FontSize - otherBaseline * 10d / 13) < .01, "live settings update every open tool and keep each font baseline");
            other.Close(); Pump();
        }
        finally { AppearanceSettings.Current.ScalePercent = originalScale; AppearanceSettings.Current.FontSize = originalFont; }
    }

    private static void CheckSidebar(string name)
    {
        var layout = (Grid)_window.FindName("ScanPageLayout");
        var settings = (Border)_window.FindName("ScanSettingsCard");
        var results = (Border)_window.FindName("ResultsCard");
        Point left = settings.TranslatePoint(new Point(0, 0), layout), right = results.TranslatePoint(new Point(0, 0), layout);
        Require(Grid.GetRow(settings) == 0 && Grid.GetRow(results) == 0 && Grid.GetColumn(settings) == 0 && Grid.GetColumn(results) == 2 && Math.Abs(left.Y - right.Y) < 1 && left.X + settings.ActualWidth < right.X, name + " keeps search on the left of results without overlap or vertical relocation");
    }

    private static void Render(string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_window.ActualWidth), (int)Math.Ceiling(_window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_outputDirectory, name)); encoder.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static MouseButtonEventArgs LeftClick(DataGrid grid, DependencyObject source, int clicks)
    {
        // Specific left-button preview events are direct events re-raised by WPF's tunnel; start that real tunnel.
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = source };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(args, clicks);
        _window.Dispatcher.Invoke(() => ((UIElement)source).RaiseEvent(args));
        return args;
    }

    private static T WithModifiers<T>(ModifierKeys modifiers, Func<T> action)
    {
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new InvalidOperationException("keyboard state unavailable");
        try
        {
            var state = new byte[256];
            if (modifiers.HasFlag(ModifierKeys.Control)) state[0x11] = state[0xA2] = 0x80;
            if (modifiers.HasFlag(ModifierKeys.Shift)) state[0x10] = state[0xA0] = 0x80;
            if (!SetKeyboardState(state)) throw new InvalidOperationException("keyboard state simulation failed");
            return action();
        }
        finally { SetKeyboardState(saved); }
    }

    private static void CheckGrid(DataGrid grid, string name)
    {
        Require(grid.SelectionMode == DataGridSelectionMode.Extended && grid.SelectionUnit == DataGridSelectionUnit.FullRow, name + " supports extended full-row selection");
        grid.SelectedItems.Clear();
        Invoke("SelectAllRecords", grid);
        Require(grid.SelectedItems.Count == grid.Items.Count && grid.Items.Count == 3, name + " SelectAll stays within its Items view");
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]);
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[0]);
        Require(row is not null, name + " has a real row container");
        RightClick(grid, row!);
        Require(grid.SelectedItems.Count == 2, name + " right click preserves an existing group");
        var menu = CurrentMenu()!;
        var snapshot = (object[])menu.DataContext;
        Require(snapshot.Length == 2 && snapshot.Contains(grid.Items[0]) && snapshot.Contains(grid.Items[1]), name + " menu captures the actual selected records");
        Require(!MenuItem(menu, "edit-address").IsEnabled && !MenuItem(menu, "hex").IsEnabled && !MenuItem(menu, "disassemble").IsEnabled && !MenuItem(menu, "trace-write").IsEnabled, name + " multi-selection disables single-record tools");
        if (grid.Items[0] is WatchRow)
            Require(MenuItem(menu, "freeze").IsEnabled && MenuItem(menu, "unfreeze").IsEnabled, name + " has explicit batch freeze and unfreeze actions");
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(grid.Items[2]);
        Require(snapshot.Length == 2 && snapshot.Contains(grid.Items[0]), name + " menu snapshot survives later selection changes");
        CloseMenu();
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]);
        var unselectedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[2]);
        RightClick(grid, unselectedRow);
        Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.SelectedItem, grid.Items[2]), name + " right click on an unselected row switches to that row");
        CloseMenu();
        RightClick(grid, grid);
        Require(CurrentMenu() is null || !CurrentMenu()!.IsOpen, name + " blank area does not open a stale selected-record menu");
    }

    private static void CheckColumnMap(DataGrid grid)
    {
        var map = typeof(MainWindow).GetMethod("ColumnEditAction", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var column in grid.Columns.OfType<DataGridBoundColumn>())
        {
            var binding = (System.Windows.Data.Binding)column.Binding;
            string? expected = binding.Path.Path switch { "AddressText" => "edit-address", "ValueText" => "edit-value", "TypeLabel" => "edit-type", "Description" => "edit-description", _ => null };
            Require(Equals(map.Invoke(null, new object[] { column }), expected), grid.Name + " double-click maps " + binding.Path.Path + " to its editor");
        }
    }

    private static void CheckWatchCtrlA(DataGrid grid)
    {
        grid.SelectedItems.Clear();
        var args = ControlA(grid);
        Require(args.Handled && grid.SelectedItems.Count == _vm.Watches.Count, "watch Ctrl+A selects all watches through the keyboard handler");
    }

    private static async Task CheckCrossPageAsync(DataGrid grid)
    {
        nint memory = VirtualAlloc(0, 4096, 0x3000, 4);
        if (memory == 0) throw new InvalidOperationException("fixture allocation failed");
        _fixtureMemory = memory; // Released after view-model shutdown so live refresh never reads a freed fixture.
        Marshal.Copy(Enumerable.Repeat(100, 1024).ToArray(), 0, memory, 1024);
        _vm.Watches.Clear(); _vm.SelectedWatch = null; // Initial UI fixtures must not prompt a process-switch confirmation.
        await _vm.AttachToProcessAsync(Environment.ProcessId);
        for (int i = 0; i < 3; i++) _vm.Watches.Add(new WatchRow { Address = (ulong)memory + (ulong)(i * 4), Type = 2, Size = 4, FrozenValue = BitConverter.GetBytes(100), ValueText = "100", Description = "live fixture " + i });
        _vm.SelectedType = _vm.TypeOptions.First(type => type.Value == 2);
        _vm.SelectedScanMode = _vm.ScanModes.First(mode => mode.Value == 0);
        _vm.SearchValue = "100";
        _vm.StartAddress = $"0x{(ulong)memory:X16}";
        _vm.EndAddress = $"0x{(ulong)memory + 4095:X16}";
        await _vm.ScanAsync(false);
        Pump();
        Require(_vm.Results.Count == 200, "real scan initially displays a 200-record page from 1,024 matches");
        Invoke("SelectAllRecords", grid);
        Require(grid.SelectedItems.Count == 200, "select current page leaves other pages unselected");
        var args = ControlA(grid);
        WaitUntil(() => !_vm.IsBusy && grid.SelectedItems.Count == 1024, 6000);
        Require(args.Handled && _vm.Results.Count == 1024 && grid.SelectedItems.Count == 1024, "result Ctrl+A loads and selects all 1,024 matches across pages");
        Require(!_vm.NextPageCommand.CanExecute(null) && _vm.PageLabel.Contains("全部"), "all-result selection marks the scope and disables page navigation");
    }

    private static KeyEventArgs ControlA(DataGrid grid)
    {
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new InvalidOperationException("keyboard state unavailable");
        try
        {
            var state = new byte[256]; state[0x11] = 0x80; state[0xA2] = 0x80;
            if (!SetKeyboardState(state)) throw new InvalidOperationException("keyboard state simulation failed");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_window)!, Environment.TickCount, Key.A) { RoutedEvent = UIElement.PreviewKeyDownEvent, Source = grid };
            Invoke("RecordGrid_PreviewKeyDown", grid, args);
            return args;
        }
        finally { SetKeyboardState(saved); }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(_vm.StatusText); Pump(); Thread.Sleep(1); }
    }

    private static void RightClick(DataGrid grid, DependencyObject source)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = source };
        Invoke("RecordGrid_MouseRightButtonDown", grid, args);
        Pump();
    }
    private static object? Invoke(string name, params object[] args) => _window.Dispatcher.Invoke(() => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(_window, args));
    private static ContextMenu? CurrentMenu() => (ContextMenu?)typeof(MainWindow).GetField("_recordContextMenu", PrivateInstance)!.GetValue(_window);
    private static void CloseMenu() { if (CurrentMenu() is { } menu) { menu.IsOpen = false; Pump(); } }
    private static MenuItem MenuItem(ContextMenu menu, string action) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, action));
    private static void Require(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); Report.Add("PASS: " + description); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] state);
}
