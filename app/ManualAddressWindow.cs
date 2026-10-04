using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MemoryStudio;

public sealed class ManualAddressWindow : Window
{
    public WatchRow? Result { get; private set; }
    public ManualAddressWindow(NativeEngine engine, int pid, bool hexadecimal)
    {
        ToolWindowLayout.Style(this, "手动添加地址", 550, 525);
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "手动地址与指针", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(ToolWindowLayout.Label("地址或模块表达式，例如 0x12345678 或 \"game.exe\"+10F0"));
        var address = ToolWindowLayout.Input("0x"); panel.Children.Add(address);
        panel.Children.Add(ToolWindowLayout.Label("指针偏移（可选；从根到目标，逗号分隔，十六进制，例如 20,18,4）"));
        var offsets = ToolWindowLayout.Input(); panel.Children.Add(offsets);
        var type = new ComboBox { ItemsSource = ValueCodec.Types, DisplayMemberPath = "Label", SelectedIndex = 0 };
        panel.Children.Add(ToolWindowLayout.Label("类型")); panel.Children.Add(type);
        var length = ToolWindowLayout.Input("4"); panel.Children.Add(ToolWindowLayout.Label("字节长度（数值类型自动确定）")); panel.Children.Add(length);
        var description = ToolWindowLayout.Input("手动地址"); panel.Children.Add(ToolWindowLayout.Label("描述")); panel.Children.Add(description);
        var error = ToolWindowLayout.Label(""); error.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ToolWindowLayout.Button("取消", (_, _) => { DialogResult = false; }));
        buttons.Children.Add(ToolWindowLayout.Button("添加", (_, _) =>
        {
            try
            {
                int kind = ((Option)type.SelectedItem).Value, size = ValueCodec.Width(kind);
                if (size == 0) size = int.Parse(length.Text, CultureInfo.InvariantCulture);
                if (size is < 1 or > 4096 || kind == 7 && (size & 1) != 0) throw new ArgumentException("请输入有效的 1–4096 字节长度，UTF-16 长度须为偶数。");
                int[] chain = offsets.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(ParseOffset).ToArray();
                if (chain.Length > 32) throw new ArgumentException("指针层数最多 32 层。");
                ulong resolved = new AddressResolver(engine, pid).Resolve(address.Text, chain);
                if (resolved == 0) throw new ArgumentException("地址不能为零。");
                byte[] bytes = engine.Read(resolved, size);
                Result = new WatchRow { Address = resolved, AddressExpression = address.Text.Trim(), PointerOffsets = chain, Type = kind, Size = size,
                    FrozenValue = bytes, Hexadecimal = hexadecimal, ValueText = ValueCodec.Format(kind, bytes, hexadecimal), Description = string.IsNullOrWhiteSpace(description.Text) ? "手动地址" : description.Text.Trim() };
                DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        panel.Children.Add(buttons);
        type.SelectionChanged += (_, _) => { int size = ValueCodec.Width(((Option)type.SelectedItem).Value); length.IsReadOnly = size > 0; if (size > 0) length.Text = size.ToString(); };
        length.IsReadOnly = true;
    }
    private static int ParseOffset(string value)
    {
        bool minus = value.StartsWith('-'); if (minus) value = value[1..];
        ulong magnitude = ValueCodec.Address(value);
        if (magnitude > (minus ? 2147483648UL : int.MaxValue)) throw new OverflowException("指针偏移超出 32 位有符号范围。");
        long offset = minus ? -(long)magnitude : (long)magnitude;
        return checked((int)offset);
    }
}
