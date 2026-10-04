using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MemoryStudio;

public sealed class PointerScannerWindow : Window
{
    private readonly int _pid;
    private readonly Action<string, int[], ulong> _add;
    private readonly NativeEngine _engine;
    private readonly ObservableCollection<PointerPath> _paths = [];
    private readonly TextBox _target = ToolWindowLayout.Input(), _depth = ToolWindowLayout.Input("3"), _offset = ToolWindowLayout.Input("1000"), _limit = ToolWindowLayout.Input("1000");
    private readonly TextBlock _status = ToolWindowLayout.Label("查找模块中的稳定根地址，再沿偏移链解析到目标地址。扫描预算有限，截断会明确提示。");
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
    private CancellationTokenSource? _cancellation;
    private bool _closed, _running;
    public PointerScannerWindow(int pid, ulong target, Action<string, int[], ulong> add)
    {
        _pid = pid; _add = add; _engine = new NativeEngine(pid); ToolWindowLayout.Style(this, "指针扫描", 880, 610);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "指针链扫描", FontSize = 21, FontWeight = FontWeights.SemiBold });
        header.Children.Add(ToolWindowLayout.Label("目标地址 · 层数（1–5）· 最大偏移（十六进制）· 最大结果数"));
        var inputs = new Grid(); foreach (double width in new[] { 0.0, 65.0, 110.0, 100.0 }) inputs.ColumnDefinitions.Add(new() { Width = width == 0 ? new(1, GridUnitType.Star) : new(width) });
        _target.Text = $"0x{target:X}"; int index = 0;
        foreach (var box in new[] { _target, _depth, _offset, _limit }) { Grid.SetColumn(box, index++); box.Margin = new(0, 0, 8, 10); inputs.Children.Add(box); }
        header.Children.Add(inputs); var buttons = new WrapPanel { Margin = new(0, 0, 0, 14) };
        buttons.Children.Add(ToolWindowLayout.Button("扫描", async (_, _) => await RunAsync(false)));
        buttons.Children.Add(ToolWindowLayout.Button("重新校验", async (_, _) => await RunAsync(true)));
        buttons.Children.Add(ToolWindowLayout.Button("停止", (_, _) => _cancellation?.Cancel()));
        buttons.Children.Add(ToolWindowLayout.Button("加入地址表", (_, _) => AddSelected()));
        header.Children.Add(buttons); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        _grid.Columns.Add(new DataGridTextColumn { Header = "根地址 / 模块", Binding = new System.Windows.Data.Binding("RootExpression"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "偏移链（根 → 目标）", Binding = new System.Windows.Data.Binding("Offsets") { Converter = new PointerOffsetsConverter() }, Width = 250 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "解析目标", Binding = new System.Windows.Data.Binding("ResolvedTarget") { StringFormat = "0x{0:X16}" }, Width = 180 });
        _grid.ItemsSource = _paths; _grid.MouseDoubleClick += (_, _) => AddSelected(); root.Children.Add(_grid);
        Closing += (_, e) => { if (_running) { _cancellation?.Cancel(); e.Cancel = true; _status.Text = "正在取消扫描，完成后可关闭窗口。"; } };
        Closed += (_, _) => { _closed = true; _cancellation?.Dispose(); _engine.Dispose(); };
    }
    private async Task RunAsync(bool recheck)
    {
        if (_running || _closed) return;
        _running = true; _cancellation = new();
        try
        {
            ulong target = ValueCodec.Address(_target.Text); if (target == 0) throw new ArgumentException("目标地址不能为零。");
            var progress = new Progress<string>(text => { if (!_closed) _status.Text = text; });
            var report = recheck
                ? await PointerScannerService.RecheckAsync(_engine, _pid, _paths.ToArray(), target, _cancellation.Token)
                : await PointerScannerService.ScanAsync(_engine, _pid, target, int.Parse(_depth.Text, CultureInfo.InvariantCulture), checked((int)ValueCodec.Address(_offset.Text)), int.Parse(_limit.Text, CultureInfo.InvariantCulture), _cancellation.Token, progress);
            if (_closed) return;
            _paths.Clear(); foreach (var path in report.Paths) _paths.Add(path); _status.Text = report.Summary;
        }
        catch (OperationCanceledException) { _status.Text = "已停止扫描；上次完成的结果保留。"; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _running = false; _cancellation?.Dispose(); _cancellation = null; }
    }
    private void AddSelected()
    {
        if (_running || _closed) return;
        foreach (var path in _grid.SelectedItems.Cast<PointerPath>()) _add(path.RootExpression, path.Offsets.ToArray(), path.ResolvedTarget);
        if (_grid.SelectedItems.Count > 0) _status.Text = $"已加入 {_grid.SelectedItems.Count:N0} 条指针链。";
    }
    private sealed class PointerOffsetsConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is IEnumerable<int> offsets ? string.Join(" → ", offsets.Select(o => $"0x{o:X}")) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
