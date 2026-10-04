using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MemoryStudio;

public partial class HexViewerWindow : Window
{
    private const int PageBytes = 1024;
    private readonly NativeEngine _engine;
    private readonly MemoryEditHistory? _edits;
    private readonly DispatcherTimer _refresh;
    private readonly bool _ownsEngine;
    private ulong _address;
    private bool _reading, _closed;

    public HexViewerWindow(NativeEngine engine, ulong initialAddress, int? processId = null, bool ownsEngine = false)
    {
        InitializeComponent();
        _engine = engine; _ownsEngine = ownsEngine;
        _edits = MemoryEditHistory.ForProcess(processId ?? engine.ProcessId);
        _address = initialAddress & ~15UL;
        AddressInput.Text = $"0x{_address:X16}";
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _refresh.Tick += async (_, _) => { if (!_reading && !_closed) await ReadAsync(); };
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await ReadAsync();
        Closed += (_, _) => { _closed = true; _refresh.Stop(); if (_ownsEngine) _engine.Dispose(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        SetInputEnabled(true);
    }
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ReadAsync();
    private async void AddressInput_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await ReadAsync(); } }
    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) return;
        _address = _address >= PageBytes ? _address - PageBytes : 0;
        AddressInput.Text = $"0x{_address:X16}"; await ReadAsync();
    }
    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) return;
        if (_address > ulong.MaxValue - PageBytes * 2) { StatusLabel.Text = "已到达地址空间末尾。"; return; }
        _address += PageBytes; AddressInput.Text = $"0x{_address:X16}"; await ReadAsync();
    }
    private async Task ReadAsync()
    {
        if (_reading || _closed) return;
        string text = AddressInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address) || address > ulong.MaxValue - PageBytes)
        { StatusLabel.Text = "请输入有效的十六进制地址，例如 0x0000012345670000。"; return; }
        ulong? selectedAddress = HexGrid.CurrentCell.Item is HexRow old ? old.RawAddress : null;
        int column = HexGrid.CurrentCell.Column?.DisplayIndex ?? 1;
        _address = address; _reading = true; SetInputEnabled(false);
        if (AutoRefreshCheck.IsChecked != true) StatusLabel.Text = "正在读取内存…";
        try
        {
            var result = await Task.Run(() => ReadPage(address));
            if (_closed) return;
            HexGrid.ItemsSource = result.Rows;
            var selected = result.Rows.FirstOrDefault(r => r.RawAddress == selectedAddress) ?? result.Rows.FirstOrDefault();
            if (selected != null) HexGrid.CurrentCell = new(selected, HexGrid.Columns[Math.Clamp(column, 0, HexGrid.Columns.Count - 1)]);
            RangeLabel.Text = $"0x{address:X16} — 0x{address + PageBytes - 1:X16}";
            StatusLabel.Text = result.ReadableBytes == PageBytes ? "已读取 1,024 字节。双击字节修改；F5 刷新当前范围。" :
                $"已读取 {result.ReadableBytes:N0} / 1,024 字节；不可读或正在追踪的页面以 ?? 显示。";
        }
        catch (ObjectDisposedException)
        { if (!_closed) { _refresh.Stop(); HexGrid.ItemsSource = null; StatusLabel.Text = "进程连接已经关闭，请重新打开内存查看器。"; } }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "读取未完成：" + ex.Message; }
        finally { _reading = false; if (!_closed) SetInputEnabled(true); }
    }
    private void SetInputEnabled(bool enabled)
    {
        AddressInput.IsEnabled = RefreshButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = enabled;
        EditByteButton.IsEnabled = enabled && _edits != null && SelectedByte() != null;
        EditRowButton.IsEnabled = enabled && _edits != null && HexGrid.CurrentCell.Item is HexRow { RawBytes: not null };
        UndoButton.IsEnabled = enabled && _edits?.CanUndo == true;
        UndoButton.ToolTip = _edits?.CanUndo == true ? "撤销：" + _edits.UndoDescription : "没有可撤销的修改";
        if (_edits == null) EditByteButton.ToolTip = EditRowButton.ToolTip = "请从当前进程重新打开，以启用编辑。";
    }
    private (List<HexRow> Rows, int ReadableBytes) ReadPage(ulong address)
    {
        var rows = new List<HexRow>(PageBytes / 16);
        byte[]? page = null;
        try { page = _engine.Read(address, PageBytes); }
        catch (ObjectDisposedException) { throw; }
        catch (InvalidOperationException) { }
        int readableBytes = 0;
        for (int offset = 0; offset < PageBytes; offset += 16)
        {
            byte[]? bytes = null;
            if (page != null) bytes = page.AsSpan(offset, 16).ToArray();
            else
            {
                try { bytes = _engine.Read(address + (ulong)offset, 16); }
                catch (ObjectDisposedException) { throw; }
                catch (InvalidOperationException) { }
            }
            if (bytes != null) readableBytes += bytes.Length;
            rows.Add(new(address + (ulong)offset, bytes));
        }
        return (rows, readableBytes);
    }
    private (ulong Address, byte Value)? SelectedByte()
    {
        if (HexGrid.CurrentCell.Item is not HexRow { RawBytes: not null } row) return null;
        int offset = (HexGrid.CurrentCell.Column?.DisplayIndex ?? 0) - 1;
        return offset is >= 0 and < 16 ? (row.RawAddress + (ulong)offset, row.RawBytes[offset]) : null;
    }
    private async Task EditAsync(ulong address, byte[] before)
    {
        if (_reading || _closed || _edits == null) return;
        bool auto = _refresh.IsEnabled; _refresh.Stop();
        try
        {
            var dialog = new MemoryBytesEditor("编辑内存字节", address, before) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            _reading = true; SetInputEnabled(false);
            await Task.Run(() => _edits.Write(_engine, address, dialog.EditedBytes!, "编辑内存字节"));
            _reading = false; await ReadAsync();
            if (!_closed) StatusLabel.Text = $"已写入 0x{address:X16} 的 {before.Length} 字节，可撤销。";
        }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "写入未完成：" + ex.Message; }
        finally { _reading = false; if (!_closed) { SetInputEnabled(true); if (auto) _refresh.Start(); } }
    }
    private async void EditByteButton_Click(object sender, RoutedEventArgs e)
    { if (SelectedByte() is { } selected) await EditAsync(selected.Address, [selected.Value]); }
    private async void EditRowButton_Click(object sender, RoutedEventArgs e)
    { if (HexGrid.CurrentCell.Item is HexRow { RawBytes: not null } row) await EditAsync(row.RawAddress, row.RawBytes); }
    private async void HexGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(HexGrid, e.OriginalSource as DependencyObject) is not DataGridRow) return;
        if (SelectedByte() is { } selected) { e.Handled = true; await EditAsync(selected.Address, [selected.Value]); }
    }
    private void HexGrid_CurrentCellChanged(object? sender, EventArgs e)
    { if (IsLoaded) SetInputEnabled(!_reading); }
    private void AutoRefreshCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_refresh == null) return;
        if (AutoRefreshCheck.IsChecked == true && !_closed) _refresh.Start(); else _refresh.Stop();
    }
    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reading || _edits?.CanUndo != true) return;
        _reading = true; SetInputEnabled(false);
        try
        {
            var result = await Task.Run(() => _edits.Undo(_engine));
            _reading = false; await ReadAsync();
            if (!_closed) StatusLabel.Text = result.Errors.Count == 0 ? "已撤销：" + result.Description : "撤销部分完成：" + string.Join("；", result.Errors);
        }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "撤销未完成：" + ex.Message; }
        finally { _reading = false; if (!_closed) SetInputEnabled(true); }
    }
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        ulong? address = SelectedByte()?.Address ?? (HexGrid.CurrentCell.Item as HexRow)?.RawAddress;
        if (address.HasValue) try { Clipboard.SetText($"0x{address:X16}"); } catch (Exception ex) { StatusLabel.Text = "复制未完成：" + ex.Message; }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; RefreshButton_Click(sender, e); }
        else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; UndoButton_Click(sender, e); }
    }
    public sealed class HexRow
    {
        public ulong RawAddress { get; }
        public byte[]? RawBytes { get; }
        public string Address { get; }
        public string[] Bytes { get; }
        public string Ascii { get; }
        public HexRow(ulong address, byte[]? bytes)
        {
            RawAddress = address; RawBytes = bytes; Address = $"{address:X16}"; Bytes = new string[16];
            var ascii = new StringBuilder(16);
            for (int i = 0; i < 16; ++i)
            {
                bool readable = bytes != null && i < bytes.Length;
                Bytes[i] = readable ? bytes![i].ToString("X2", CultureInfo.InvariantCulture) : "??";
                ascii.Append(!readable ? '·' : bytes![i] is >= 32 and <= 126 ? (char)bytes[i] : '·');
            }
            Ascii = ascii.ToString();
        }
    }
}
