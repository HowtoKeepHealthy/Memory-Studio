using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MemoryStudio;

internal static class Program
{
    private static readonly List<string> Report = [];
    private static readonly List<Window> Windows = [];
    private static string _output = "";
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = Path.GetFullPath(args[0]); Directory.CreateDirectory(_output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryStudio", "appearance.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        int result = 0;
        nint memory = 0;
        MainViewModel? vm = null;
        NativeEngine? engine = null;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ThemeCheck;component/Theme.xaml") });
            AppearanceSettings.TransientSession = true; AppearanceSettings.SuspendSizeMemory = true;
            ThemeManager.Apply("dark"); AppearanceSettings.Initialize();
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) AppearanceSettings.Apply(window); }));
            Require(JsonSerializer.Deserialize<AppearancePreferences>("{\"ScalePercent\":100}")!.ThemeId == "dark", "existing preferences without ThemeId preserve dark");
            Require(ThemeManager.Normalize("unknown") == "dark", "invalid stored theme falls back to dark");

            memory = Marshal.AllocHGlobal(4096);
            Marshal.Copy(Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray(), 0, memory, 4096);
            engine = new NativeEngine(Environment.ProcessId);
            vm = new MainViewModel();
            var main = Show(new MainWindow { DataContext = vm }); app.MainWindow = main;
            Task attach = vm.AttachToProcessAsync(Environment.ProcessId); Wait(attach);
            vm.StartAddress = $"0x{memory:X}"; vm.EndAddress = $"0x{(ulong)memory + 4095:X}";
            foreach (var item in new[] { (Offset: 16, Type: 2, Value: "100", Name: "生命值"), (Offset: 32, Type: 2, Value: "2500", Name: "金币"), (Offset: 48, Type: 4, Value: "1.25", Name: "移动速度") })
            {
                byte[] bytes = ValueCodec.Parse(item.Type, item.Value);
                Marshal.Copy(bytes, 0, memory + item.Offset, bytes.Length);
                vm.SelectedType = vm.TypeOptions.Single(o => o.Value == item.Type); vm.SelectedScanMode = vm.ScanModes[0]; vm.SearchValue = item.Value;
                Wait(vm.ScanAsync(false)); vm.SelectedResult = vm.Results.Single(); vm.AddSelectedCommand.Execute(null); vm.SelectedWatch!.Description = item.Name;
            }
            var hex = Show(new HexViewerWindow(engine, (ulong)memory));
            var disassembly = Show(new DisassemblyWindow(engine, Environment.ProcessId, (ulong)memory));
            var trace = Show(new AccessTraceWindow(Environment.ProcessId, (ulong)memory, 4, false));
            var editor = Show(new RecordEditorWindow(new RecordEditRequest(RecordEditKind.Type, "100", 2, 4, "已选择 3 项")));
            var manual = Show(new ManualAddressWindow(engine, Environment.ProcessId, false));
            var structure = Show(new StructureViewerWindow(engine, (ulong)memory));
            var settingsWindow = Show(new SettingsWindow { DataContext = AppearanceSettings.Current });
            var bytesEditor = Show(new MemoryBytesEditor("机器码预览", (ulong)memory, [0x90, 0xC3], 64));
            Pump(1200);

            foreach (string theme in new[] { "dark", "paper", "dark" })
            {
                AppearanceSettings.Current.ThemeId = theme; ThemeManager.Apply(theme); Pump(80);
                if (theme == "paper") Show(new MemoryBytesEditor("白色主题下新建的编辑器", (ulong)memory, [0x90, 0xC3]));
                var text = Brush("TextBrush"); var muted = Brush("MutedBrush"); var background = Brush("BackgroundBrush");
                Require(Contrast(text, background) >= 7, theme + " body text contrast >= 7");
                Require(Contrast(muted, Brush("SurfaceBrush")) >= 4.5, theme + " disabled/menu secondary contrast >= 4.5");
                Require(Contrast(Brush("AccentOnBrush"), Brush("AccentBrush")) >= 4.5, theme + " primary button contrast >= 4.5");
                Require(Colour((Brush)app.Resources[SystemColors.ControlBrushKey]) == Colour(background), theme + " scroll corner follows background");
                foreach (Window window in Windows)
                {
                    Require(Colour(window.Background) == Colour(background), theme + " updates " + window.GetType().Name + " background");
                    Require(Colour(window.Foreground) == Colour(text), theme + " updates " + window.GetType().Name + " foreground");
                }
                CheckMenu(hex, (DataGrid)hex.FindName("HexGrid"), theme + " HEX");
                CheckMenu(disassembly, (DataGrid)disassembly.FindName("InstructionGrid"), theme + " disassembly");
                CheckMenu(trace, (DataGrid)trace.FindName("HitGrid"), theme + " trace");
                var primary = (Button)hex.FindName("RefreshButton");
                var primaryText = Descendants(primary).OfType<TextBlock>().First(t => t.Text == "读取内存");
                Require(Colour(primaryText.Foreground) == Colour(Brush("AccentOnBrush")), theme + " primary button actual glyphs inherit contrast colour");
                Render(main, theme + "-main.png"); Render(hex, theme + "-hex.png"); Render(disassembly, theme + "-disassembly.png");
                if (theme == "paper") { Render(settingsWindow, "paper-settings.png"); Render(trace, "paper-trace.png"); Render(manual, "paper-manual.png"); }
            }
            var selector = (ComboBox)settingsWindow.FindName("ThemeSelector");
            selector.SelectedValue = "paper"; Pump(80);
            Require(AppearanceSettings.Current.ThemeId == "paper" && ThemeManager.CurrentThemeId == "paper", "theme selector changes preferences and open windows immediately");
            AppearanceSettings.Current.FontSize = 18; AppearanceSettings.Current.ScalePercent = 125; Pump(80);
            double before = ((TextBlock)hex.FindName("StatusLabel")).FontSize;
            ThemeManager.Apply("dark"); ThemeManager.Apply("paper"); Pump(80);
            Require(((TextBlock)hex.FindName("StatusLabel")).FontSize == before, "theme switching never changes user font sizing");
            Require(((ScaleTransform)((FrameworkElement)hex.Content).LayoutTransform).ScaleX == 1.25, "theme switching preserves UI scale");
            var prefs = JsonSerializer.Deserialize<AppearancePreferences>(JsonSerializer.Serialize(AppearanceSettings.Current))!;
            Require(prefs.ThemeId == "paper", "theme preference round-trips in appearance JSON");
        }
        catch (Exception ex) { Report.Add("FAIL: " + ex); result = 1; }
        finally
        {
            foreach (var window in Windows.AsEnumerable().Reverse().ToArray()) window.Close();
            Pump(100); vm?.Dispose(); engine?.Dispose();
            if (memory != 0) Marshal.FreeHGlobal(memory);
            Application.Current?.Shutdown();
            byte[]? after = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
            if (!(originalSettings is null && after is null || originalSettings is not null && after is not null && originalSettings.SequenceEqual(after))) { Report.Add("FAIL: personal preferences changed during transient test"); result = 1; }
            else { _checks++; Report.Add("PASS: transient test leaves personal preferences byte-for-byte unchanged"); }
            Report.Add($"Checks: {_checks}"); File.WriteAllLines(Path.Combine(_output, "theme-results.txt"), Report);
            foreach (string line in Report) Console.WriteLine(line);
        }
        return result;
    }

    private static T Show<T>(T window) where T : Window
    {
        window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -15000;
        Windows.Add(window); window.Show(); return window;
    }
    private static void CheckMenu(Window window, DataGrid grid, string label)
    {
        ContextMenu menu = grid.ContextMenu;
        // Measure the actual detached menu directly. ContextMenu intentionally rejects a
        // visual parent; no native popup opens on the user's desktop during this check.
        menu.Visibility = Visibility.Visible; menu.ApplyTemplate();
        menu.Measure(new Size(450, 600)); menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout(); Pump(40);
        Require(Colour(menu.Background) == Colour(Brush("SurfaceBrush")), label + " context menu background follows theme");
        var item = menu.Items.OfType<MenuItem>().First();
        item.ApplyTemplate();
        Require(Colour(item.Foreground) == Colour(Brush("TextBrush")), label + " enabled menu text explicit semantic colour");
        item.IsEnabled = false; Pump(20);
        Require(Colour(item.Foreground) == Colour(Brush("MutedBrush")), label + " disabled menu text explicit readable colour");
        var header = Descendants(item).OfType<TextBlock>().FirstOrDefault(t => t.Text == (string)item.Header);
        Require(header is not null && Colour(header.Foreground) == Colour(Brush("MutedBrush")), label + " actual disabled header glyphs are readable");
        item.IsEnabled = true;
        typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.SetValue(item, true); Pump(20);
        var border = (Border)item.Template.FindName("MenuItemBorder", item);
        Require(Colour(border.Background) == Colour(Brush("HoverBrush")), label + " hover remains readable");
        Require(header is not null && Colour(header.Foreground) == Colour(Brush("TextBrush")), label + " actual hover header glyphs render semantic text");
        RenderElement(menu, label.Replace(' ', '-').ToLowerInvariant() + "-menu.png");
        typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.SetValue(item, false);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var node in Descendants(child)) yield return node; } }
    private static void Render(Window window, string name)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(_output, name)); encoder.Save(file);
    }
    private static void RenderElement(FrameworkElement element, string name)
    {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(_output, name)); encoder.Save(file);
    }
    private static SolidColorBrush Brush(string name) => (SolidColorBrush)Application.Current.Resources[name];
    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;
    private static double Contrast(Brush a, Brush b)
    {
        static double L(Color c) { static double V(byte b) { double n = b / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); } return .2126 * V(c.R) + .7152 * V(c.G) + .0722 * V(c.B); }
        double x = L(Colour(a)), y = L(Colour(b)); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); _checks++; Report.Add("PASS: " + label); }
    private static void Wait(Task task) { var until = DateTime.UtcNow.AddSeconds(15); while (!task.IsCompleted && DateTime.UtcNow < until) Pump(20); if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); }
    private static void Pump(int ms = 20)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
