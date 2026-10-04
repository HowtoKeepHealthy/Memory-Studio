using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MemoryStudio;

public sealed class StructureField : ObservableObject
{
    private string _value = "—", _bytes = "";
    public int Offset { get; set; }
    public int Type { get; set; } = 2;
    public int Size { get; set; } = 4;
    public string Name { get; set; } = "字段";
    public string OffsetText => $"+0x{Offset:X4}";
    public string TypeLabel => ValueCodec.TypeLabel(Type);
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Bytes { get => _bytes; set => Set(ref _bytes, value); }
}
public sealed class StructureViewerWindow : Window
{
    private readonly NativeEngine _engine;
    private readonly bool _ownsEngine;
    private readonly ObservableCollection<StructureField> _fields = [];
    private readonly TextBox _address = ToolWindowLayout.Input(), _length = ToolWindowLayout.Input("100");
    private readonly TextBlock _status = ToolWindowLayout.Label("");
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reading, _closed;
    public StructureViewerWindow(NativeEngine engine, ulong address, bool ownsEngine = false)
    {
        _engine = engine; _ownsEngine = ownsEngine; ToolWindowLayout.Style(this, "结构查看", 830, 600);
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "结构与字段", FontSize = 21, FontWeight = FontWeights.SemiBold });
        header.Children.Add(ToolWindowLayout.Label("基址及结构长度（十六进制）。默认按 4 字节整数列出；双击类型或数值可编辑字段。"));
        var inputs = new Grid(); inputs.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); inputs.ColumnDefinitions.Add(new() { Width = new GridLength(95) });
        _address.Text = $"0x{address:X}"; _length.Margin = new Thickness(10, 0, 0, 7); Grid.SetColumn(_length, 1);
        inputs.Children.Add(_address); inputs.Children.Add(_length); header.Children.Add(inputs);
        var buttons = new WrapPanel { Margin = new Thickness(0, 3, 0, 12) };
        buttons.Children.Add(ToolWindowLayout.Button("解析结构", async (_, _) => { BuildFields(); await RefreshAsync(); }));
        buttons.Children.Add(ToolWindowLayout.Button("刷新", async (_, _) => await RefreshAsync()));
        buttons.Children.Add(ToolWindowLayout.Button("增加字段", (_, _) => AddField()));
        buttons.Children.Add(ToolWindowLayout.Button("删除所选字段", (_, _) => { foreach (var field in _grid.SelectedItems.Cast<StructureField>().ToArray()) _fields.Remove(field); }));
        header.Children.Add(buttons); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        foreach (var (title, binding, width) in new[] { ("偏移", "OffsetText", 90.0), ("名称", "Name", 140.0), ("类型", "TypeLabel", 140.0), ("当前数值", "Value", 170.0), ("字节", "Bytes", 190.0) })
            _grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new System.Windows.Data.Binding(binding), Width = width });
        _grid.ItemsSource = _fields; root.Children.Add(_grid);
        _grid.MouseDoubleClick += async (_, e) => await EditFieldAsync(e);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { BuildFields(); await RefreshAsync(); _timer.Start(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); if (_ownsEngine) _engine.Dispose(); };
    }
    private void BuildFields()
    {
        try
        {
            int length = checked((int)ValueCodec.Address(_length.Text));
            if (length is < 1 or > 65536) throw new ArgumentException("结构长度需要 1–0x10000 字节。");
            _fields.Clear(); for (int offset = 0; offset + 4 <= length; offset += 4) _fields.Add(new() { Offset = offset, Name = $"字段_{offset:X4}" });
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void AddField()
    {
        var request = new RecordEditRequest(RecordEditKind.Type, "", 2, 4, "添加结构字段");
        var edit = RecordEditorWindow.Edit(this, request);
        if (edit != null)
        {
            int offset = _fields.Count == 0 ? 0 : _fields.Max(f => f.Offset + f.Size);
            _fields.Add(new() { Offset = offset, Type = edit.Type, Size = edit.ByteSize, Name = $"字段_{offset:X4}" });
        }
    }
    private async Task RefreshAsync()
    {
        if (_reading || _closed || _fields.Count == 0) return;
        _reading = true;
        try
        {
            ulong address = ValueCodec.Address(_address.Text); var fields = _fields.ToArray();
            var values = await Task.Run(() => fields.Select(field =>
            {
                try { byte[] bytes = _engine.Read(checked(address + (ulong)field.Offset), field.Size); return (field, value: ValueCodec.Format(field.Type, bytes), raw: Convert.ToHexString(bytes)); }
                catch { return (field, value: "不可读取", raw: ""); }
            }).ToArray());
            if (_closed) return;
            foreach (var item in values) { item.field.Value = item.value; item.field.Bytes = item.raw; }
            _status.Text = $"{fields.Length:N0} 个字段 · 每秒刷新 · 基址 0x{address:X16}";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _reading = false; }
    }
    private async Task EditFieldAsync(MouseButtonEventArgs e)
    {
        if (_grid.SelectedItem is not StructureField field || _reading) return;
        var cell = FindCell(e.OriginalSource as DependencyObject);
        int column = cell?.Column.DisplayIndex ?? 3;
        try
        {
            if (column == 0)
            {
                var edit = RecordEditorWindow.Edit(this, new(RecordEditKind.Value, field.Offset.ToString(CultureInfo.InvariantCulture), 2, 4, "字段偏移（十进制，可为零）"));
                if (edit != null) { int offset = BitConverter.ToInt32(ValueCodec.Parse(2, edit.Text, edit.Hexadecimal)); if (offset is < 0 or > 65536) throw new ArgumentException("偏移须在 0–65536 之间。"); field.Offset = offset; }
            }
            else if (column == 1)
            {
                var edit = RecordEditorWindow.Edit(this, new(RecordEditKind.Description, field.Name, field.Type, field.Size, field.OffsetText));
                if (edit != null) field.Name = edit.Text;
            }
            else if (column == 2)
            {
                var edit = RecordEditorWindow.Edit(this, new(RecordEditKind.Type, "", field.Type, field.Size, field.OffsetText));
                if (edit != null) { field.Type = edit.Type; field.Size = edit.ByteSize; }
            }
            else
            {
                var edit = RecordEditorWindow.Edit(this, new(RecordEditKind.Value, field.Value, field.Type, field.Size, field.OffsetText));
                if (edit != null)
                {
                    ulong address = checked(ValueCodec.Address(_address.Text) + (ulong)field.Offset);
                    byte[] value = ValueCodec.Parse(field.Type, edit.Text, edit.Hexadecimal);
                    await Task.Run(() => MemoryEditHistory.ForProcess(_engine.ProcessId).Write(_engine, address, value, "修改结构字段"));
                }
            }
            _grid.Items.Refresh(); await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private static DataGridCell? FindCell(DependencyObject? item)
    {
        while (item != null) { if (item is DataGridCell cell) return cell; item = System.Windows.Media.VisualTreeHelper.GetParent(item); }
        return null;
    }
}
