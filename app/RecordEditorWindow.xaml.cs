using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MemoryStudio;

public enum RecordEditKind { Value, Address, Type, Description }
public sealed record RecordEditRequest(RecordEditKind Kind, string InitialText, int Type, int ByteSize, string AddressText, bool Hexadecimal = false);
public sealed record RecordEditResult(string Text, int Type, int ByteSize, bool Hexadecimal = false);

public partial class RecordEditorWindow : Window
{
    private readonly RecordEditRequest _request;
    private bool _initializing = true;
    private RecordEditResult? _result;
    private bool _hexadecimal;

    public RecordEditorWindow(RecordEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind) || request.Type is < 0 or > 8)
            throw new ArgumentException("记录的编辑项目或数据类型无效。", nameof(request));
        _request = request;
        _hexadecimal = request.Hexadecimal;
        InitializeComponent();
        DataContext = null;
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        string subject = request.Kind switch
        {
            RecordEditKind.Value => "修改数值",
            RecordEditKind.Address => "修改地址",
            RecordEditKind.Type => "修改数据类型",
            _ => "修改描述"
        };
        Title = $"Memory Studio · {subject}";
        HeadingLabel.Text = subject;
        ContextLabel.Text = $"{request.AddressText}  ·  {ValueCodec.TypeLabel(request.Type)}  ·  {request.ByteSize:N0} 字节";
        ContentInput.Text = request.InitialText ?? "";
        RadixInput.IsChecked = _hexadecimal;
        RadixInput.Visibility = request.Kind == RecordEditKind.Value && request.Type <= 5 ? Visibility.Visible : Visibility.Collapsed;
        ByteSizeInput.Text = request.ByteSize.ToString(CultureInfo.InvariantCulture);
        TypeInput.ItemsSource = ValueCodec.Types;
        TypeInput.SelectedItem = ValueCodec.Types.First(option => option.Value == request.Type);
        if (request.Kind == RecordEditKind.Type)
        {
            TextPane.Visibility = Visibility.Collapsed;
            TypePane.Visibility = Visibility.Visible;
            HintLabel.Text = "选择新的读取与显示类型。数值类型使用固定长度，字符串和字节序列使用指定长度。";
            UpdateTypeLength();
        }
        else
        {
            FieldLabel.Text = request.Kind switch { RecordEditKind.Value => "新数值", RecordEditKind.Address => "新地址（十六进制）", _ => "描述" };
            ContentInput.MaxLength = request.Kind switch { RecordEditKind.Description => 200, RecordEditKind.Address => 20, _ => 16384 };
            if (request.Kind != RecordEditKind.Description) ContentInput.FontFamily = new("Consolas");
            HintLabel.Text = request.Kind switch
            {
                RecordEditKind.Address => "输入非零十六进制地址，支持 0x 前缀。例如 0x0000012345670000。",
                RecordEditKind.Description => "输入 1–200 个字符，便于识别这条地址记录。",
                _ => ValueHint(request.Type, request.ByteSize)
            };
        }
        _initializing = false;
        Loaded += (_, _) =>
        {
            if (request.Kind == RecordEditKind.Type) TypeInput.Focus();
            else { ContentInput.Focus(); ContentInput.SelectAll(); }
        };
    }

    public static RecordEditResult? Edit(Window owner, RecordEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var dialog = new RecordEditorWindow(request) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    /// <summary>Validate one edit without accessing a target process or opening a dialog.</summary>
    public static RecordEditResult Validate(RecordEditRequest request, string text, int type, int byteSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        text ??= "";
        if (!Enum.IsDefined(request.Kind) || request.Type is < 0 or > 8)
            throw new ArgumentException("记录的编辑项目或数据类型无效。");
        switch (request.Kind)
        {
            case RecordEditKind.Value:
            {
                if (request.ByteSize is < 1 or > 4096) throw new ArgumentException("记录的数据长度必须是 1–4096 字节。");
                byte[] bytes;
                try { bytes = ValueCodec.Parse(request.Type, text, request.Hexadecimal); }
                catch (FormatException) { throw new ArgumentException(request.Type == 8 ? "字节序列格式有误：每个字节使用两位十六进制数，例如 DE AD BE EF。" : "输入格式有误：整数和浮点数使用十进制。"); }
                catch (OverflowException) { throw new ArgumentException("数值超出了此数据类型的有效范围。"); }
                if (bytes.Length != request.ByteSize)
                    throw new ArgumentException($"输入编码后是 {bytes.Length:N0} 字节，需要保持此记录的 {request.ByteSize:N0} 字节长度。");
                return new(text, request.Type, request.ByteSize, request.Hexadecimal);
            }
            case RecordEditKind.Address:
            {
                ulong address;
                try { address = ValueCodec.Address(text); }
                catch (FormatException) { throw new ArgumentException("请输入有效的十六进制地址，可使用 0x 前缀。"); }
                catch (OverflowException) { throw new ArgumentException("地址超出了 64 位地址范围。"); }
                if (address == 0) throw new ArgumentException("地址不能为零。");
                return new($"0x{address:X16}", request.Type, request.ByteSize);
            }
            case RecordEditKind.Type:
            {
                if (type is < 0 or > 8) throw new ArgumentException("请选择有效的数据类型。");
                int scalarWidth = ValueCodec.Width(type);
                int size = scalarWidth > 0 ? scalarWidth : byteSize;
                if (size is < 1 or > 4096) throw new ArgumentException("读取长度必须是 1–4096 字节。");
                if (type == 7 && (size & 1) != 0) throw new ArgumentException("UTF-16 的字节长度必须是偶数。");
                return new(ValueCodec.TypeLabel(type), type, size);
            }
            case RecordEditKind.Description:
            {
                string description = text.Trim();
                if (description.Length == 0) throw new ArgumentException("描述不能为空。");
                if (description.Length > 200) throw new ArgumentException("描述最多为 200 个字符。");
                return new(description, request.Type, request.ByteSize);
            }
            default: throw new ArgumentException("不支持此编辑项目。");
        }
    }

    private static string ValueHint(int type, int byteSize) => type switch
    {
        6 => $"输入 UTF-8 文本，编码后须保持 {byteSize:N0} 字节。中文字符可能占用多个字节。",
        7 => $"输入 UTF-16 文本，编码后须保持 {byteSize:N0} 字节。长度按编码字节计算。",
        8 => $"输入十六进制字节，可用空格或连字符分隔。须保持 {byteSize:N0} 字节，例如 DE AD BE EF。",
        4 or 5 => "输入有限浮点数，使用小数点；支持科学计数法，例如 1.25 或 1e3。",
        _ => "输入十进制整数。保存前会检查当前类型的数值范围。"
    };

    private void UpdateTypeLength()
    {
        if (TypeInput.SelectedItem is not Option option) return;
        int scalarWidth = ValueCodec.Width(option.Value);
        ByteSizeInput.IsReadOnly = scalarWidth > 0;
        if (scalarWidth > 0)
        {
            ByteSizeInput.Text = scalarWidth.ToString(CultureInfo.InvariantCulture);
            LengthHint.Text = "数值类型的长度由数据类型自动确定。";
        }
        else
        {
            if (!int.TryParse(ByteSizeInput.Text, out int size) || size is < 1 or > 4096) size = option.Value == 7 ? 2 : 1;
            if (option.Value == 7 && (size & 1) != 0) ++size;
            ByteSizeInput.Text = size.ToString(CultureInfo.InvariantCulture);
            LengthHint.Text = option.Value == 7 ? "UTF-16 使用 1–4096 字节中的偶数长度。" : "输入 1–4096 字节的读取长度。";
        }
    }

    private void TypeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateTypeLength();
        ErrorLabel.Text = "";
    }
    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initializing) ErrorLabel.Text = "";
    }
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int type = (TypeInput.SelectedItem as Option)?.Value ?? _request.Type;
            int size = _request.ByteSize;
            if (_request.Kind == RecordEditKind.Type && !int.TryParse(ByteSizeInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out size))
                throw new ArgumentException("读取长度必须是 1–4096 字节的整数。");
            _result = Validate(_request with { Hexadecimal = _hexadecimal }, ContentInput.Text, type, size);
            DialogResult = true;
        }
        catch (ArgumentException ex) { ErrorLabel.Text = ex.Message; }
    }
    private void RadixInput_Click(object sender, RoutedEventArgs e)
    {
        bool requested = RadixInput.IsChecked == true;
        try
        {
            ContentInput.Text = ValueCodec.Format(_request.Type, ValueCodec.Parse(_request.Type, ContentInput.Text, _hexadecimal), requested);
            _hexadecimal = requested;
            HintLabel.Text = requested && _request.Type is 4 or 5 ? "浮点十六进制显示 IEEE 754 位模式；切换回十进制会恢复数值。" : ValueHint(_request.Type, _request.ByteSize);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        { RadixInput.IsChecked = _hexadecimal; ErrorLabel.Text = ex.Message; }
    }
}
