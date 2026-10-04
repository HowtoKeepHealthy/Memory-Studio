using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MemoryStudio;

public static class Program
{
    private static readonly List<string> Lines = [];
    private static string Output = "";
    private static int Checks;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint bytes, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint bytes, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint bytes, uint protection, out uint old);
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL " + message); ++Checks; Lines.Add("PASS " + message); Console.WriteLine(Lines[^1]); }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var descendant in Visuals<T>(child)) yield return descendant; } }
    private static async Task Wait(Func<bool> predicate, string message)
    { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.ElapsedMilliseconds > 10000) throw new Exception("TIMEOUT " + message); await Task.Delay(30); } }
    [STAThread] public static void Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/memory-analysis"); Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) });
        AppearanceSettings.TransientSession = true; AppearanceSettings.SuspendSizeMemory = true;
        ThemeManager.Apply("dark"); AppearanceSettings.Initialize();
        app.Startup += async (_, _) =>
        {
            try { await Run(); Lines.Add($"ALL {Checks} PASSED"); File.WriteAllLines(Path.Combine(Output, "analysis-results.txt"), Lines); app.Shutdown(0); }
            catch (Exception ex) { Lines.Add(ex.ToString()); Console.Error.WriteLine(ex); File.WriteAllLines(Path.Combine(Output, "analysis-results.txt"), Lines); app.Shutdown(1); }
        };
        app.Run();
    }
    private static async Task Run()
    {
        nint allocation = VirtualAlloc(0, 12288, 0x3000, 4); if (allocation == 0) throw new Exception("VirtualAlloc failed");
        ulong address = (ulong)allocation.ToInt64() + 128, ip = (ulong)allocation.ToInt64() + 4096 + 64;
        using var engine = new NativeEngine(Environment.ProcessId);
        MemoryAnalysisWindow? window = null; var owner = new Window { Title = "Analysis integration owner", Width = 700, Height = 430 };
        try
        {
            Marshal.WriteInt32((nint)(long)address, 100);
            byte[] code = Enumerable.Repeat((byte)0x90, 4096).ToArray();
            byte[] instructions = [0x89, 0x41, 0x04, 0x8B, 0x41, 0x04, 0x48, 0x8D, 0x51, 0x04, 0x83, 0xF8, 0x64, 0x75, 0x02, 0xE8, 44, 0, 0, 0, 0xC3];
            instructions.CopyTo(code, 64); Marshal.Copy(code, 0, allocation + 4096, code.Length);
            if (!VirtualProtect(allocation + 4096, 4096, 0x20, out _)) throw new Exception("VirtualProtect RX failed");
            var request = new MemoryAnalysisRequest(Environment.ProcessId, address, 2, 4, "Health | <label>\nline");
            var dataReport = MemoryAnalysisReportService.Capture(engine, request);
            Check(dataReport.Value.DecodedValue == "100" && dataReport.Value.BytesHex == "64000000" && dataReport.Value.BitPattern == "0x00000064", "current integer bytes and little endian value are collected from real memory");
            Check(dataReport.Region is { IsExecutable: false, Protection: "RW", Kind: "PRIVATE" } && dataReport.Location.Module == null, "private data region is distinguished from executable image code");
            Check(dataReport.CodeContexts.All(context => !context.AnchorKnown && context.Instructions.All(row => !row.BoundaryKnown)) && dataReport.CapturedAccesses.Count == 0, "arbitrary scanned data is never promoted to a known instruction or access source");
            Check(dataReport.Notes.Any(note => note.Contains("不能确定实际读写来源")), "missing trace evidence is explicit in the report");
            Check(MemoryAnalysisReportService.RangesOverlap(address, 4, address + 3, 4) && !MemoryAnalysisReportService.RangesOverlap(address, 4, address + 4, 4) && !MemoryAnalysisReportService.RangesOverlap(ulong.MaxValue - 1, 4, 0, 4), "range association accepts partial overlap and rejects adjacent or overflowing ranges");
            var module = new ProcessModuleInfo("test.dll", 0x1000, 0x200, 0x1040);
            Check(MemoryAnalysisReportService.Locate(0x1088, [module]).Rva == "0x0000000000000088" && MemoryAnalysisReportService.Locate(0x1200, [module]).Module == null, "module-relative addresses use the correct exclusive module range");
            var registers = new ulong[16]; registers[1] = address - 4; registers[0] = 100;
            var sample = new AccessTraceSample(1, ip, address, 42, 64, instructions[..3], "mov [rcx+4], eax", TracedMemoryAccess.Write, address, 4, registers, 0x202, 1234, true);
            var hit = new AccessTraceHit(sample); hit.Add(sample with { Sequence = 2, TimestampMs = 1300 });
            var evidence = MemoryTraceEvidence.FromHit(Environment.ProcessId, hit);
            registers[1] = 0;
            Check(evidence.Count == 2 && evidence.Sequence == 2 && evidence.Registers["RCX"] == MemoryAnalysisReportService.Hex(address - 4), "trace DTO copies the latest sample, count and register values without retaining live arrays");
            var foreign = evidence with { ProcessId = Environment.ProcessId + 1 };
            var unrelated = evidence with { MemoryAddress = address + 4 };
            var report = MemoryAnalysisReportService.Capture(engine, request with { TraceEvidence = [foreign, unrelated, evidence] });
            Check(report.CapturedAccesses.Count == 1 && report.CapturedAccesses[0].InstructionPointer == MemoryAnalysisReportService.Hex(ip) && report.CapturedAccesses[0].Count == 2, "only matching process and operand ranges are included as captured evidence");
            var anchored = report.CodeContexts.First();
            Check(anchored.AnchorKnown && anchored.AnchorAddress == MemoryAnalysisReportService.Hex(ip) && anchored.Instructions.Where(row => ulong.Parse(row.Address[2..], System.Globalization.NumberStyles.HexNumber) < ip).All(row => !row.BoundaryKnown), "captured IP anchors forward decode while reverse rows remain uncertain");
            var store = anchored.Instructions.Single(row => row.Address == MemoryAnalysisReportService.Hex(ip));
            Check(store.BoundaryKnown && store.MemoryOperands.Any(text => text.Contains("写入 4 字节") && text.Contains("RCX")) && store.Registers.Any(text => text.Contains("EAX: 读取")), "Iced identifies store operand width, base and source register");
            var load = anchored.Instructions.Single(row => row.Address == MemoryAnalysisReportService.Hex(ip + 3));
            Check(load.MemoryOperands.Any(text => text.Contains("读取 4 字节")) && load.Registers.Any(text => text.Contains("写入")), "Iced distinguishes load memory reads from register writes");
            var lea = anchored.Instructions.Single(row => row.Address == MemoryAnalysisReportService.Hex(ip + 6));
            Check(lea.Explanation.Contains("不读取") && lea.MemoryOperands.All(text => !text.Contains("读取 8 字节")), "LEA explanation does not invent a memory read");
            var compare = anchored.Instructions.Single(row => row.Address == MemoryAnalysisReportService.Hex(ip + 10));
            var conditional = anchored.Instructions.Single(row => row.Address == MemoryAnalysisReportService.Hex(ip + 13));
            Check(compare.Explanation.Contains("不保存") && conditional.Explanation.Contains("标志位"), "comparison and conditional branch have useful bounded Chinese explanations");
            var call = anchored.Instructions.Single(row => row.Address == MemoryAnalysisReportService.Hex(ip + 15));
            Check(call.DirectTarget?.Address == MemoryAnalysisReportService.Hex(ip + 64) && call.FlowControl == "Call" && call.Explanation.Contains("调用"), "relative call export resolves the real direct target");
            Check(report.CodeContexts.Last().AnchorKnown == false && report.CodeContexts.Last().Instructions.All(row => !row.BoundaryKnown), "data neighborhood stays uncertain even when a separate trace IP is known");
            string markdown = report.ToMarkdown(), json = report.ToJson(), prompt = report.ToAiPrompt();
            using var parsed = JsonDocument.Parse(json);
            Check(parsed.RootElement.GetProperty("location").GetProperty("address").GetString() == MemoryAnalysisReportService.Hex(address) && parsed.RootElement.GetProperty("capturedAccesses")[0].GetProperty("registers").GetProperty("RCX").ValueKind == JsonValueKind.String, "JSON uses exact hexadecimal strings for 64 bit addresses and registers");
            Check(markdown.Contains("Health \\| &lt;label&gt;") && !markdown.Contains("Health | <label>\nline") && markdown.Contains("不是 UTC"), "Markdown escapes target text and labels trace uptime correctly");
            Check(prompt.Contains("已观察事实") && prompt.Contains("可验证假设") && prompt.Contains("静态解码当作访问来源") && prompt.Contains(ip.ToString("X16")), "AI prompt carries captured context and asks for evidence grounded conclusions");
            File.WriteAllText(Path.Combine(Output, "sample-analysis.md"), markdown, new UTF8Encoding(false)); File.WriteAllText(Path.Combine(Output, "sample-analysis.json"), json, new UTF8Encoding(false));
            Check(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Output, "sample-analysis.md"))) == markdown && File.ReadAllText(Path.Combine(Output, "sample-analysis.json")) == json, "UTF8 Markdown and JSON sample exports round trip exactly");
            // Make the next page truly inaccessible and repeat, ensuring partial failures are represented.
            if (!VirtualProtect(allocation + 8192, 4096, 0x01, out _)) throw new Exception("VirtualProtect noaccess failed");
            var unreadable = MemoryAnalysisReportService.Capture(engine, request with { Address = (ulong)allocation.ToInt64() + 8192 });
            Check(unreadable.Value.BytesHex == null && unreadable.Value.ReadError != null && unreadable.CodeContexts.Any(context => context.ReadError != null), "unreadable value and code retain useful partial report and explicit errors");
            bool bad = false; try { MemoryAnalysisReportService.Capture(engine, request with { ByteSize = 8 }); } catch (ArgumentException) { bad = true; }
            Check(bad, "mismatched scalar width is rejected before decoding");
            owner.Show(); var owned = new NativeEngine(Environment.ProcessId);
            window = new MemoryAnalysisWindow(owned, request with { TraceEvidence = [evidence] }, ownsEngine: true) { Owner = owner }; window.Show();
            await Wait(() => !window.IsLoading && window.Report != null, "real analysis window report");
            window.UpdateLayout();
            Check(Field<TabControl>(window, "ReportTabs").SelectedIndex == 0 && Field<TextBox>(window, "ReportText").IsVisible && Field<TextBox>(window, "ReportText").ActualHeight > 200,
                "report tab and content have a real visible viewport under the production theme");
            Check(owner.IsEnabled && window.Report!.CapturedAccesses.Count == 1 && Field<TextBox>(window, "ReportText").Text.Contains("已捕获的访问来源"), "modeless analysis window shows actual report while owner remains enabled");
            Field<TabControl>(window, "ReportTabs").SelectedIndex = 1;
            await Task.Delay(80); window.UpdateLayout();
            Check(Field<DataGrid>(window, "CodeGrid").IsVisible && Field<DataGrid>(window, "CodeGrid").ActualHeight > 200 &&
                Field<DataGrid>(window, "CodeGrid").SelectedItem is MemoryAnalysisInstruction { BoundaryKnown: true },
                "code tab displays real instruction rows with the captured anchor selected");
            var grid = Field<DataGrid>(window, "CodeGrid");
            await Wait(() =>
            {
                var row = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem);
                if (row == null) return false;
                double y = row.TransformToAncestor(grid).Transform(new Point(0, 0)).Y;
                return y >= grid.ColumnHeaderHeight && y + row.ActualHeight < grid.ActualHeight;
            }, "captured IP becomes actually visible after tab layout");
            var selectedRow = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem);
            Check(selectedRow != null && selectedRow.TransformToAncestor(grid).Transform(new Point(0, 0)).Y >= grid.ColumnHeaderHeight &&
                selectedRow.TransformToAncestor(grid).Transform(new Point(0, 0)).Y + selectedRow.ActualHeight < grid.ActualHeight,
                "captured instruction anchor is actually visible in the code viewport");
            Save(window, "analysis-code.png"); Field<TabControl>(window, "ReportTabs").SelectedIndex = 0; await Task.Delay(60);
            Field<Button>(window, "CopyPromptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Clipboard.GetText() == window.Report!.ToAiPrompt(), "real copy button produces the complete AI prompt");
            Check(Visuals<TextBlock>(Field<Button>(window, "CopyPromptButton")).First().Foreground is SolidColorBrush darkText &&
                darkText.Color == ((SolidColorBrush)Application.Current.Resources["AccentOnBrush"]).Color,
                "dark theme primary button glyph uses the intended contrasting foreground");
            Save(window, "analysis-dark.png");
            ThemeManager.Apply("paper"); await Task.Delay(100);
            Check(((SolidColorBrush)window.Background).Color == ((SolidColorBrush)Application.Current.Resources["BackgroundBrush"]).Color, "analysis window updates to paper palette while open");
            Check(Visuals<TextBlock>(Field<Button>(window, "CopyPromptButton")).First().Foreground is SolidColorBrush paperText &&
                paperText.Color == ((SolidColorBrush)Application.Current.Resources["AccentOnBrush"]).Color,
                "paper theme primary button glyph uses the intended contrasting foreground");
            Save(window, "analysis-paper.png"); window.Close(); window = null;
            bool disposed = false; try { owned.Read(address, 4); } catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "closing analysis releases its owned memory session");
        }
        finally { window?.Close(); owner.Close(); VirtualFree(allocation, 0, 0x8000); }
    }
    private static void Save(Window window, string name)
    {
        window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(Output, name)); png.Save(stream);
    }
}
