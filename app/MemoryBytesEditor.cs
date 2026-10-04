using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MemoryStudio;

/// <summary>A byte patch preview. User input is parsed directly and never passed to a shell.</summary>
public sealed class MemoryBytesEditor : Window
{
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly int _length;
    private readonly ComboBox? _mode;
    private readonly CheckBox? _pad;
    private readonly ulong _address;
    private readonly int? _bitness;
    private bool _working;
    public byte[]? EditedBytes { get; private set; }

    public MemoryBytesEditor(string title, ulong address, byte[] original, int? bitness = null)
    {
        Title = "Memory Studio · " + title;
        Width = 750; Height = bitness.HasValue ? 440 : 330; MinWidth = 530; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        FontFamily = new("Microsoft YaHei UI, Segoe UI"); FontSize = 13;
        _length = original.Length;
        _address = address; _bitness = bitness;
        var root = new Grid { Margin = new Thickness(22) };
        foreach (var size in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = size });
        var heading = new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold };
        root.Children.Add(heading);
        var description = new TextBlock { Text = $"0x{address:X16} · {original.Length} 字节。输入相同长度的十六进制机器码；写入后可撤销。", Margin = new(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(description, 1); root.Children.Add(description);
        if (bitness.HasValue)
        {
            var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12) };
            _mode = new ComboBox { Width = 220, ItemsSource = new[] { "十六进制机器码", $"Intel / NASM · {bitness} 位" }, SelectedIndex = 0 };
            _pad = new CheckBox { Content = "不足范围时用 NOP 补足", VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 0, 0) };
            options.Children.Add(_mode); options.Children.Add(_pad); Grid.SetRow(options, 2); root.Children.Add(options);
        }
        _input = new TextBox { Text = string.Join(" ", original.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))), AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas"), MinHeight = 75 };
        Grid.SetRow(_input, 3); root.Children.Add(_input);
        _status = new TextBlock { Text = "支持空格、换行分隔或连续十六进制字节。", TextWrapping = TextWrapping.Wrap, MaxHeight = 90, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 10, 0, 10) };
        Grid.SetRow(_status, 4); root.Children.Add(_status);
        if (_mode != null) _mode.SelectionChanged += (_, _) =>
        {
            _input.Text = _mode.SelectedIndex == 0 ? string.Join(" ", original.Select(b => b.ToString("X2"))) : "nop";
            _status.Text = _mode.SelectedIndex == 0 ? "支持空格、换行分隔或连续十六进制字节。" :
                "Intel / NASM 语法，支持多条指令与局部标签。地址及位数固定；先预览机器码，再写入。";
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 9, 0) };
        var save = new Button { Content = "写入这些字节", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var preview = new Button { Content = "预览机器码", Margin = new(0, 0, 9, 0) };
        preview.Click += async (_, _) =>
        {
            if (_working) return;
            _working = true; save.IsEnabled = preview.IsEnabled = false; SetEditingEnabled(false);
            try
            {
                byte[] bytes = await ParseInputAsync();
                _status.Text = $"预览 · {bytes.Length} 字节：" + string.Join(" ", bytes.Take(64).Select(b => b.ToString("X2"))) + (bytes.Length > 64 ? " …" : "") +
                    (_bitness.HasValue ? "\n" + string.Join(" · ", DisassemblyService.Decode(bytes, _address, _bitness.Value).Select(r => r.Instruction).Take(3)) : "");
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _working = false; save.IsEnabled = preview.IsEnabled = true; SetEditingEnabled(true); }
        };
        save.Click += async (_, _) =>
        {
            if (_working) return;
            _working = true; save.IsEnabled = preview.IsEnabled = false; SetEditingEnabled(false);
            try
            {
                byte[] bytes = await ParseInputAsync();
                if (IsVisible) { EditedBytes = bytes; DialogResult = true; }
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _working = false; save.IsEnabled = preview.IsEnabled = true; SetEditingEnabled(true); }
        };
        actions.Children.Add(preview); actions.Children.Add(cancel); actions.Children.Add(save); Grid.SetRow(actions, 5); root.Children.Add(actions);
        Content = root;
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private void SetEditingEnabled(bool enabled)
    {
        _input.IsEnabled = enabled;
        if (_mode != null) _mode.IsEnabled = enabled;
        if (_pad != null) _pad.IsEnabled = enabled;
    }

    private async Task<byte[]> ParseInputAsync()
    {
        string text = _input.Text;
        bool assemble = _mode?.SelectedIndex == 1;
        bool pad = _pad?.IsChecked == true;
        byte[] bytes = assemble ? await Task.Run(() => AssemblyService.Assemble(text, _address, _bitness!.Value)) : ParseBytes(text);
        if (bytes.Length > _length) throw new ArgumentException($"编译结果为 {bytes.Length} 字节，超过选中范围 {_length} 字节。请选择更大的完整指令范围。");
        if (bytes.Length < _length)
        {
            if (!pad) throw new ArgumentException($"必须覆盖 {_length} 字节，当前为 {bytes.Length}。代码补丁可勾选「不足范围时用 NOP 补足」。");
            bytes = bytes.Concat(Enumerable.Repeat((byte)0x90, _length - bytes.Length)).ToArray();
        }
        return bytes;
    }

    public static byte[] ParseBytes(string input)
    {
        string compact = string.Concat(input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (compact.Length is < 2 or > 8192 || compact.Length % 2 != 0)
            throw new ArgumentException("请输入 1 至 4096 个完整十六进制字节，例如 90 90 或 9090。");
        try { return Convert.FromHexString(compact); }
        catch (FormatException) { throw new ArgumentException("只允许十六进制字节（00 至 FF），使用空格或换行分隔。"); }
    }
}
