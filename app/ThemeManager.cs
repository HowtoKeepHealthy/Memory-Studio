using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MemoryStudio;

/// <summary>Semantic colours shared by windows, tool windows and detached popup trees.</summary>
public static class ThemeManager
{
    private static bool _hooksRegistered;
    private static readonly List<WeakReference<FrameworkElement>> Popups = [];
    public static string CurrentThemeId { get; private set; } = "dark";
    public static bool IsDark => CurrentThemeId == "dark";
    public static event EventHandler? Changed;
    public static string Normalize(string? id) => id == "paper" ? "paper" : "dark";

    // Order is shared by both palettes. Body and address font choices stay with their controls.
    private static readonly string[] Keys = ["BackgroundBrush", "SurfaceBrush", "CardBrush", "InputBrush", "LineBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentSoftBrush", "HoverBrush", "NavBrush", "HeaderBrush", "TableHeaderBrush", "AlternatingRowBrush", "SelectedRowBrush", "StrongLineBrush", "AccentTextBrush", "BadgeBrush", "BadgeTextBrush", "AddressBrush", "SuccessBrush", "WarningBrush", "ErrorBrush", "AccentOnBrush", "AccentHoverBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush"];
    private static readonly string[] Dark = ["#0B1018", "#111925", "#151E2C", "#0F1722", "#263244", "#EDF3FB", "#91A2B9", "#57E0C1", "#183D3C", "#202D3F", "#101722", "#111925", "#101926", "#111B29", "#193A3D", "#537088", "#57E0C1", "#1E3637", "#91E9D7", "#90B9E4", "#57E0C1", "#E8BE71", "#FFBF8A", "#09221E", "#9AF1DD", "#3A4C63", "#5B7490"];
    private static readonly string[] Paper = ["#F4F1E9", "#FBF9F3", "#FFFEFA", "#FFFEFA", "#DAD8CC", "#282E2C", "#5D6862", "#A63D32", "#F2E4DD", "#ECEAE1", "#EAEDE5", "#F6F3EC", "#EEEDE4", "#F6F5EF", "#E1EBE3", "#A7B4A9", "#94382E", "#E4ECE3", "#305B48", "#315E68", "#34674E", "#825C16", "#A03D32", "#FFFEFA", "#BD4D40", "#B7BFB4", "#8B9C8E"];

    public static void Initialize()
    {
        AppearanceSettings.Initialize();
        Apply(AppearanceSettings.Current.ThemeId);
    }

    /// <summary>persist=false never reads or writes settings; use it in screenshot/test hosts.</summary>
    public static void Apply(string id, bool persist = false)
    {
        id = Normalize(id);
        if (persist)
        {
            AppearanceSettings.Initialize();
            AppearanceSettings.Current.ThemeId = id;
        }
        CurrentThemeId = id;
        if (Application.Current is not { } app) return;
        RegisterHooks();
        var values = IsDark ? Dark : Paper;
        for (int i = 0; i < Keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
            brush.Freeze();
            app.Resources[Keys[i]] = brush;
        }
        app.Resources["HeadingFontFamily"] = new FontFamily(IsDark ? "Microsoft YaHei UI, Segoe UI" : "SimSun, Songti SC, Microsoft YaHei UI");
        SetSystemBrush(app, SystemColors.ControlBrushKey, "BackgroundBrush");
        SetSystemBrush(app, SystemColors.ControlTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.WindowBrushKey, "InputBrush");
        SetSystemBrush(app, SystemColors.WindowTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.MenuBrushKey, "SurfaceBrush");
        SetSystemBrush(app, SystemColors.MenuTextBrushKey, "TextBrush");
        SetSystemBrush(app, SystemColors.GrayTextBrushKey, "MutedBrush");
        SetSystemBrush(app, SystemColors.HighlightBrushKey, "SelectedRowBrush");
        SetSystemBrush(app, SystemColors.HighlightTextBrushKey, "TextBrush");
        foreach (Window window in app.Windows) ApplyWindow(window);
        foreach (var weak in Popups.ToArray())
        {
            if (weak.TryGetTarget(out var popup)) UpdatePopupResources(popup);
            else Popups.Remove(weak);
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void SetSystemBrush(Application app, ResourceKey key, string paletteKey) => app.Resources[key] = app.Resources[paletteKey];

    private static void RegisterHooks()
    {
        if (_hooksRegistered) return;
        _hooksRegistered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) ApplyWindow(window); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is ContextMenu menu) RegisterPopup(menu); }));
        EventManager.RegisterClassHandler(typeof(ToolTip), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is ToolTip tip) RegisterPopup(tip); }));
    }

    private static void RegisterPopup(FrameworkElement popup)
    {
        if (!Popups.Any(weak => weak.TryGetTarget(out var known) && ReferenceEquals(known, popup))) Popups.Add(new(popup));
        UpdatePopupResources(popup);
    }

    private static void UpdatePopupResources(FrameworkElement popup)
    {
        if (Application.Current is not { } app) return;
        // Closed ContextMenus are detached from the window tree. A resource notification
        // on their own dictionary also invalidates cached menu-item/header resources.
        foreach (string key in Keys) popup.Resources[key] = app.Resources[key];
        popup.Resources["HeadingFontFamily"] = app.Resources["HeadingFontFamily"];
        popup.Resources[SystemColors.ControlBrushKey] = app.Resources["BackgroundBrush"];
    }

    public static void ApplyWindow(Window window)
    {
        if (window.AllowsTransparency && !window.ShowInTaskbar && window.WindowStyle == WindowStyle.None) return;
        ApplyLegacyColors(window);
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        try
        {
            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    // Older code-built tool windows may have a local palette brush. Replace only recognised
    // local constants; never override bindings, resources, diagnostic colours or font settings.
    private static void ApplyLegacyColors(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            if (element.ContextMenu is { } menu) RegisterPopup(menu);
            var properties = new List<DependencyProperty>();
            if (element is Control) properties.AddRange([Control.BackgroundProperty, Control.ForegroundProperty, Control.BorderBrushProperty]);
            if (element is Panel) properties.Add(Panel.BackgroundProperty);
            if (element is Border) properties.AddRange([Border.BackgroundProperty, Border.BorderBrushProperty]);
            if (element is TextBlock) properties.Add(TextBlock.ForegroundProperty);
            if (element is Shape) properties.AddRange([Shape.FillProperty, Shape.StrokeProperty]);
            foreach (var property in properties)
            {
                var source = DependencyPropertyHelper.GetValueSource(element, property);
                if (source.BaseValueSource != BaseValueSource.Local || source.IsExpression || BindingOperations.IsDataBound(element, property)) continue;
                if (element.GetValue(property) is not SolidColorBrush brush || brush.Color.A != 255) continue;
                string colour = $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
                int index = Array.IndexOf(Dark, colour);
                if (index >= 0) element.SetResourceReference(property, Keys[index]);
            }
        }
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) return;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) ApplyLegacyColors(VisualTreeHelper.GetChild(root, i));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
