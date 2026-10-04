using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MemoryStudio;

public partial class MemoryAnalysisWindow : Window
{
    private readonly NativeEngine _engine;
    private readonly MemoryAnalysisRequest _request;
    private readonly bool _ownsEngine;
    private bool _closed;
    public MemoryAnalysisReport? Report { get; private set; }
    public bool IsLoading { get; private set; }

    public MemoryAnalysisWindow(NativeEngine engine, MemoryAnalysisRequest request, bool ownsEngine = false)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        _engine = engine; _request = request; _ownsEngine = ownsEngine;
        TargetLabel.Text = $"PID {request.ProcessId}  /  {MemoryAnalysisReportService.Hex(request.Address)}  /  {ValueCodec.TypeLabel(request.Type)}";
        SourceInitialized += (_, _) => ThemeManager.ApplyWindow(this);
        Loaded += async (_, _) => await RefreshAsync();
        ReportTabs.SelectionChanged += (_, _) => QueueCodeAnchor();
        CodeGrid.Loaded += (_, _) => QueueCodeAnchor();
        Closed += (_, _) => { _closed = true; if (_ownsEngine) _engine.Dispose(); };
        SetButtons();
    }
    public async Task RefreshAsync()
    {
        if (IsLoading || _closed) return;
        IsLoading = true; SetButtons(); StatusLabel.Text = "正在收集本机只读快照及指令摘要…";
        try
        {
            var report = await Task.Run(() => MemoryAnalysisReportService.Capture(_engine, _request));
            if (_closed) return;
            Report = report;
            TargetLabel.Text = $"{report.ProcessName} · PID {report.ProcessId} · {report.Architecture} · {report.Location.Address}" +
                (report.Location.Module == null ? " · 未落在已列出的模块内" : $" · {report.Location.Module} + {report.Location.Rva}");
            string value = report.Value.DecodedValue ?? "不可读取";
            ValueLabel.Text = $"{report.Value.TypeLabel} · {report.Value.ByteSize} 字节 · 当前值 {Short(value, 180)}";
            EvidenceLabel.Text = report.CapturedAccesses.Count == 0 ?
                "尚无匹配的访问捕获。附近指令只作静态解释；请追踪后再确认实际读写来源。" :
                $"包含 {report.CapturedAccesses.Count} 条匹配的访问聚合记录及最近寄存器样本。捕获字节与当前快照分开记录。";
            ReportText.Text = report.ToMarkdown(); PromptText.Text = report.ToAiPrompt(); JsonText.Text = report.ToJson();
            CodeGrid.ItemsSource = report.CodeContexts.SelectMany(context => context.Instructions).ToArray();
            CodeGrid.SelectedItem = report.CodeContexts.SelectMany(context => context.Instructions).FirstOrDefault(row => row.BoundaryKnown);
            StatusLabel.Text = $"快照完成 · {report.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 可复制或导出，自行选择 AI 工具解释。";
        }
        catch (Exception ex) { if (!_closed) StatusLabel.Text = "报告未完成：" + ex.Message; }
        finally { IsLoading = false; if (!_closed) SetButtons(); }
    }
    private static string Short(string text, int length) => text.Length <= length ? text : text[..length] + "…";
    private void QueueCodeAnchor() => _ = Dispatcher.InvokeAsync(() =>
    {
        if (_closed || !CodeGrid.IsVisible || CodeGrid.SelectedItem is not MemoryAnalysisInstruction selected) return;
        CodeGrid.UpdateLayout();
        if (FindVisual<ScrollViewer>(CodeGrid) is { } scroll)
        {
            scroll.ScrollToHorizontalOffset(0);
            scroll.ScrollToVerticalOffset(Math.Max(0, CodeGrid.Items.IndexOf(selected) - scroll.ViewportHeight / 2));
        }
    }, DispatcherPriority.Loaded);
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void SetButtons()
    {
        RefreshButton.IsEnabled = !IsLoading;
        CopyPromptButton.IsEnabled = CopyReportButton.IsEnabled = ExportButton.IsEnabled = !IsLoading && Report != null;
    }
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void CopyPromptButton_Click(object sender, RoutedEventArgs e) => Copy(Report?.ToAiPrompt(), "已复制报告和分析提示，可自行粘贴到 AI 对话。");
    private void CopyReportButton_Click(object sender, RoutedEventArgs e) => Copy(Report?.ToMarkdown(), "已复制 Markdown 报告。");
    private void Copy(string? text, string status)
    {
        if (text == null) return;
        try { Clipboard.SetText(text); StatusLabel.Text = status; }
        catch (Exception ex) { StatusLabel.Text = "复制未完成：" + ex.Message; }
    }
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (Report == null) return;
        var dialog = new SaveFileDialog { Title = "导出本机地址分析", Filter = "Markdown 报告 (*.md)|*.md|JSON 报告 (*.json)|*.json",
            FileName = $"memory-analysis-{_request.ProcessId}-{_request.Address:X}.md", AddExtension = true, DefaultExt = ".md" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            bool json = dialog.FilterIndex == 2 || Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(dialog.FileName, json ? Report.ToJson() : Report.ToMarkdown(), new UTF8Encoding(false));
            StatusLabel.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex) { StatusLabel.Text = "导出未完成：" + ex.Message; }
    }
}
