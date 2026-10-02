using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace MemoryStudio;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _pickerTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private WindowProcessPicker.Highlight? _pickerHighlight;
    private WindowTarget? _pickerTarget;
    private Cursor? _previousCursor;
    private bool _picking;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        _pickerTimer.Tick += (_, _) => UpdatePickerTarget();
        Closed += (_, _) => EndPickerCapture();
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

    private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.SelectedItem is null)
            return;

        // Header and scrollbar double clicks must not add an unrelated selected row.
        if (ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is not DataGridRow)
            return;

        if (DataContext?.GetType().GetProperty("AddSelectedCommand")?.GetValue(DataContext) is ICommand command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
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
