using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MemoryStudio;

public sealed class AppearancePreferences : INotifyPropertyChanged
{
    private double _scalePercent = 100, _fontSize = 13;
    private bool _rememberWindowSize = true;
    public double ScalePercent { get => _scalePercent; set => Update(ref _scalePercent, double.IsFinite(value) ? Math.Clamp(value, 60, 150) : 100); }
    public double FontSize { get => _fontSize; set => Update(ref _fontSize, double.IsFinite(value) ? Math.Clamp(value, 10, 20) : 13); }
    public bool RememberWindowSize { get => _rememberWindowSize; set => Update(ref _rememberWindowSize, value); }
    public Dictionary<string, SavedWindowSize> Windows { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Update<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record SavedWindowSize(double Width, double Height, bool Maximized);

/// <summary>One shared display preference for all workbench windows. Call Apply from the application's Window.Loaded hook.</summary>
public static class AppearanceSettings
{
    private sealed class WindowInfo
    {
        public required FrameworkElement Root;
        public required ScaleTransform Scale;
        public double FontRatio = 1;
        public bool FontRefreshQueued;
    }
    private sealed record FontBaseline(DependencyProperty Property, double Size);
    private static readonly Dictionary<Window, WindowInfo> OpenWindows = [];
    private static readonly ConditionalWeakTable<DependencyObject, FontBaseline> Fonts = new();
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoryStudio", "appearance.json");
    private static readonly DispatcherTimer SaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private static bool _initialized;
    private static SettingsWindow? _settingsWindow;
    public static AppearancePreferences Current { get; private set; } = new();
    public static event EventHandler? Changed;
    /// <summary>Screenshot and test hosts may suppress restoring or saving window geometry.</summary>
    public static bool SuspendSizeMemory { get; set; }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (File.Exists(SettingsPath)) Current = JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(SettingsPath)) ?? new();
            Current.Windows ??= [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { Current = new(); }
        Current.PropertyChanged += (_, _) =>
        {
            foreach (var pair in OpenWindows.ToArray()) UpdateWindow(pair.Key, pair.Value);
            QueueSave();
            Changed?.Invoke(null, EventArgs.Empty);
        };
        SaveTimer.Tick += (_, _) => { SaveTimer.Stop(); Save(); };
        if (Application.Current is { } app) app.Exit += (_, _) => Save();
    }

    public static void Apply(Window window)
    {
        Initialize();
        // Process-picker highlights are native, hollow overlay windows; their borders must retain screen coordinates.
        if (window.Content is not FrameworkElement root || window.AllowsTransparency && !window.ShowInTaskbar && window.WindowStyle == WindowStyle.None) return;
        if (!OpenWindows.TryGetValue(window, out var info))
        {
            info = new WindowInfo { Root = root, Scale = new ScaleTransform() };
            OpenWindows.Add(window, info);
            root.LayoutTransform = info.Scale;
            if (Current.RememberWindowSize && !SuspendSizeMemory && Current.Windows.TryGetValue(window.GetType().Name, out var size))
            {
                var work = SystemParameters.WorkArea;
                if (double.IsFinite(size.Width) && double.IsFinite(size.Height))
                {
                    window.Width = Math.Clamp(size.Width, window.MinWidth, Math.Max(window.MinWidth, work.Width));
                    window.Height = Math.Clamp(size.Height, window.MinHeight, Math.Max(window.MinHeight, work.Height));
                    if (size.Maximized && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip) window.WindowState = WindowState.Maximized;
                }
            }
            window.PreviewMouseWheel += Window_PreviewMouseWheel;
            window.SizeChanged += Window_SizeChanged;
            window.StateChanged += Window_StateChanged;
            root.LayoutUpdated += (_, _) => QueueFontRefresh(window, info);
            window.Closed += (_, _) =>
            {
                RememberSize(window);
                OpenWindows.Remove(window);
                Save();
            };
        }
        UpdateWindow(window, info);
    }

    private static void UpdateWindow(Window window, WindowInfo info)
    {
        info.Scale.ScaleX = info.Scale.ScaleY = Current.ScalePercent / 100;
        ApplyFonts(window, info.FontRatio, Current.FontSize / 13);
        info.FontRatio = Current.FontSize / 13;
    }

    private static void ApplyFonts(DependencyObject root, double previousRatio, double ratio)
    {
        // Capture before assigning any ancestor's font so inherited values are not multiplied twice.
        var nodes = DescendantsAndSelf(root).ToArray();
        foreach (var node in nodes)
        {
            DependencyProperty? property = node switch { Control => Control.FontSizeProperty, TextBlock => TextBlock.FontSizeProperty, _ => null };
            if (property is null || Fonts.TryGetValue(node, out _) || BindingOperations.IsDataBound(node, property)) continue;
            double size = (double)node.GetValue(property);
            var source = DependencyPropertyHelper.GetValueSource(node, property);
            if (source.BaseValueSource == BaseValueSource.Inherited) size /= previousRatio;
            Fonts.Add(node, new FontBaseline(property, size));
        }
        foreach (var node in nodes)
            if (Fonts.TryGetValue(node, out var font)) node.SetCurrentValue(font.Property, font.Size * ratio);
    }

    private static IEnumerable<DependencyObject> DescendantsAndSelf(DependencyObject root)
    {
        yield return root;
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(root); i < count; i++)
            foreach (var child in DescendantsAndSelf(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void QueueFontRefresh(Window window, WindowInfo info)
    {
        if (info.FontRefreshQueued || info.FontRatio == 1) return;
        info.FontRefreshQueued = true;
        window.Dispatcher.BeginInvoke(() =>
        {
            info.FontRefreshQueued = false;
            if (!OpenWindows.ContainsKey(window)) return;
            ApplyFonts(window, info.FontRatio, info.FontRatio);
        }, DispatcherPriority.Background);
    }

    public static void ApplyPopup(ContextMenu menu)
    {
        Initialize();
        menu.FontSize = 12;
        menu.Opened += (_, _) =>
        {
            menu.LayoutTransform = new ScaleTransform(Current.ScalePercent / 100, Current.ScalePercent / 100);
            ApplyFonts(menu, 1, Current.FontSize / 13);
        };
    }

    private static void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Current.ScalePercent = Math.Clamp(Math.Round(Current.ScalePercent / 5) * 5 + Math.Sign(e.Delta) * 5, 60, 150);
        e.Handled = true;
    }

    private static void Window_SizeChanged(object sender, SizeChangedEventArgs e) { if (sender is Window window) RememberSize(window); }
    private static void Window_StateChanged(object? sender, EventArgs e) { if (sender is Window window) RememberSize(window); }
    private static void RememberSize(Window window)
    {
        if (SuspendSizeMemory || !Current.RememberWindowSize || window.WindowState == WindowState.Minimized || !window.IsLoaded) return;
        Rect bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight) : window.RestoreBounds;
        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0) return;
        Current.Windows[window.GetType().Name] = new SavedWindowSize(bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
        QueueSave();
    }

    private static void QueueSave() { SaveTimer.Stop(); SaveTimer.Start(); }
    private static void Save()
    {
        if (!_initialized) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void ShowSettings(Window owner)
    {
        Initialize();
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow { Owner = owner, DataContext = Current };
        try { _settingsWindow.ShowDialog(); }
        finally { _settingsWindow = null; }
    }
}
