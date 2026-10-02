using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace MemoryStudio;

public partial class HexViewerWindow : Window
{
    private const int PageBytes = 1024;
    private readonly NativeEngine _engine;
    private ulong _address;
    private bool _reading, _closed;

    public HexViewerWindow(NativeEngine engine, ulong initialAddress)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        _engine = engine;
        _address = initialAddress & ~15UL;
        AddressInput.Text = $"0x{_address:X16}";
        Loaded += async (_, _) => await ReadAsync();
        Closed += (_, _) => _closed = true;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReadAsync();

    private async void AddressInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ReadAsync();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) return;
        _address = _address >= PageBytes ? _address - PageBytes : 0;
        AddressInput.Text = $"0x{_address:X16}";
        await ReadAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) return;
        if (_address > ulong.MaxValue - PageBytes * 2) { StatusLabel.Text = "已到达地址空间末尾。"; return; }
        _address += PageBytes;
        AddressInput.Text = $"0x{_address:X16}";
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        if (_reading || _closed) return;
        string text = AddressInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address) || address > ulong.MaxValue - PageBytes)
        {
            StatusLabel.Text = "请输入有效的十六进制地址，例如 0x0000012345670000。";
            return;
        }
        _address = address;
        _reading = true;
        SetInputEnabled(false);
        StatusLabel.Text = "正在读取内存…";
        try
        {
            var result = await Task.Run(() => ReadPage(address));
            if (_closed) return;
            HexGrid.ItemsSource = result.Rows;
            RangeLabel.Text = $"0x{address:X16}  —  0x{address + PageBytes - 1:X16}";
            StatusLabel.Text = result.ReadableBytes == PageBytes
                ? "已读取 1,024 字节。不可打印的字符以 · 显示；点击读取内存可更新内容。"
                : result.ReadableBytes == 0
                    ? "该范围无法读取（??）。请检查地址是否有效、目标进程是否仍在运行。"
                    : $"已读取 {result.ReadableBytes:N0} / 1,024 字节；不可读取的区域以 ?? 显示。";
        }
        catch (ObjectDisposedException)
        {
            if (!_closed) { HexGrid.ItemsSource = null; StatusLabel.Text = "进程连接已经关闭或更换，请从当前进程重新打开内存查看器。"; }
        }
        catch (Exception ex)
        {
            if (!_closed) { HexGrid.ItemsSource = null; StatusLabel.Text = $"读取未完成：{ex.Message}"; }
        }
        finally
        {
            _reading = false;
            if (!_closed) SetInputEnabled(true);
        }
    }

    private void SetInputEnabled(bool enabled)
    {
        AddressInput.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        PreviousButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
    }

    private (List<HexRow> Rows, int ReadableBytes) ReadPage(ulong address)
    {
        var rows = new List<HexRow>(PageBytes / 16);
        byte[]? page = null;
        try { page = _engine.Read(address, PageBytes); }
        catch (InvalidOperationException) { /* A page can cross an unreadable boundary; retry each row. */ }
        int readableBytes = 0;
        for (int offset = 0; offset < PageBytes; offset += 16)
        {
            byte[]? bytes = null;
            if (page is not null) bytes = page.AsSpan(offset, 16).ToArray();
            else
            {
                try { bytes = _engine.Read(address + (ulong)offset, 16); }
                catch (InvalidOperationException) { }
            }
            if (bytes is not null) readableBytes += bytes.Length;
            rows.Add(new HexRow(address + (ulong)offset, bytes));
        }
        return (rows, readableBytes);
    }

    public sealed class HexRow
    {
        public string Address { get; }
        public string[] Bytes { get; }
        public string Ascii { get; }
        public HexRow(ulong address, byte[]? bytes)
        {
            Address = $"{address:X16}";
            Bytes = new string[16];
            var ascii = new StringBuilder(16);
            for (int i = 0; i < 16; i++)
            {
                bool readable = bytes is not null && i < bytes.Length;
                Bytes[i] = readable ? bytes![i].ToString("X2", CultureInfo.InvariantCulture) : "??";
                ascii.Append(!readable ? '·' : bytes![i] is >= 32 and <= 126 ? (char)bytes[i] : '·');
            }
            Ascii = ascii.ToString();
        }
    }
}
