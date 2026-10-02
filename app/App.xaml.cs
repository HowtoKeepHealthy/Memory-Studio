using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace MemoryStudio;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private nint _previewMemory;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string report = e.Args.Length > 1 ? e.Args[1] : Path.Combine(AppContext.BaseDirectory, "self-test.txt");
            int code = await SmokeTests.RunAsync(report); Shutdown(code); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "error.log");
            try { File.AppendAllText(path, $"{DateTime.Now:O} {args.Exception}\n"); } catch { }
            if (e.Args.Length > 0 && e.Args[0] == "--screenshot") { args.Handled = true; Shutdown(1); return; }
            MessageBox.Show($"操作出现错误：{args.Exception.Message}\n详细信息已保存到 error.log。", "Memory Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        _viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        if (e.Args.Length > 1 && e.Args[0] == "--screenshot")
        {
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -15000; window.Top = -15000;
            if (e.Args.Length > 3 && int.TryParse(e.Args[2], out int width) && int.TryParse(e.Args[3], out int height)) { window.Width = width; window.Height = height; }
        }
        window.Show();
        if (e.Args.Length > 1 && e.Args[0] == "--screenshot")
        {
            string path = Path.GetFullPath(e.Args[1]);
            if (e.Args.Contains("--sample")) await PopulatePreviewAsync(_viewModel);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            Window capture = window;
            if (e.Args.Contains("--hex") && _previewMemory != 0)
            {
                _viewModel.OpenMemoryViewerCommand.Execute(null);
                capture = Windows.OfType<HexViewerWindow>().Last();
                capture.ShowInTaskbar = false; capture.Left = -15000; capture.Top = -15000;
                await Task.Delay(400);
            }
            if (e.Args.Contains("--disassembly") && _previewMemory != 0)
            {
                _viewModel.SelectedResult = null;
                _viewModel.SelectedWatch = null;
                _viewModel.OpenDisassemblyCommand.Execute(null);
                capture = Windows.OfType<DisassemblyWindow>().Last();
                capture.ShowInTaskbar = false; capture.Left = -15000; capture.Top = -15000;
                await Task.Delay(800);
            }
            if (e.Args.Contains("--edit-type"))
            {
                capture = new RecordEditorWindow(new RecordEditRequest(RecordEditKind.Type, "100", 2, 4, "已选择 12 项"))
                {
                    Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -15000, Top = -15000
                };
                capture.Show();
                await Task.Delay(100);
            }
            capture.UpdateLayout();
            var content = capture.Content as FrameworkElement;
            int imageWidth = (int)Math.Ceiling(content != null ? content.ActualWidth + content.Margin.Left + content.Margin.Right : capture.ActualWidth);
            int imageHeight = (int)Math.Ceiling(content != null ? content.ActualHeight + content.Margin.Top + content.Margin.Bottom : capture.ActualHeight);
            var bitmap = new RenderTargetBitmap(imageWidth, imageHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(capture);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            Shutdown();
        }
    }
    private async Task PopulatePreviewAsync(MainViewModel vm)
    {
        _previewMemory = Marshal.AllocHGlobal(4096);
        Marshal.Copy(new byte[4096], 0, _previewMemory, 4096);
        Marshal.WriteInt32(_previewMemory, 16, 100);
        Marshal.WriteInt32(_previewMemory, 32, 2500);
        Marshal.Copy(BitConverter.GetBytes(1.25f), 0, _previewMemory + 48, 4);
        vm.SelectedProcess = vm.Processes.Single(p => p.Id == Environment.ProcessId);
        vm.AttachCommand.Execute(null);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!vm.IsAttached || vm.IsBusy)
        {
            if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Preview attach timed out");
            await Task.Delay(10);
        }
        vm.StartAddress = $"0x{_previewMemory:X}";
        vm.EndAddress = $"0x{(ulong)_previewMemory + 4096:X}";
        foreach (var item in new[] { (Type: 2, Value: "100", Name: "生命值"), (Type: 2, Value: "2500", Name: "金币"), (Type: 4, Value: "1.25", Name: "移动速度") })
        {
            vm.SelectedType = vm.TypeOptions.Single(t => t.Value == item.Type);
            vm.SearchValue = item.Value; vm.SelectedScanMode = vm.ScanModes[0];
            await vm.ScanAsync(false); vm.SelectedResult = vm.Results.Single();
            vm.AddSelectedCommand.Execute(null); vm.SelectedWatch!.Description = item.Name;
        }
        vm.SelectedType = vm.TypeOptions.Single(t => t.Value == 2);
        vm.SearchValue = "100"; await vm.ScanAsync(false);
        vm.SelectedResult = vm.Results.Single(); vm.SelectedWatch = vm.Watches[0];
        vm.Watches[0].IsFrozen = true;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        if (_previewMemory != 0) Marshal.FreeHGlobal(_previewMemory);
        base.OnExit(e);
    }
}
