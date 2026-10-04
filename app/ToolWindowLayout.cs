using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MemoryStudio;

internal static class ToolWindowLayout
{
    public static void Style(Window window, string title, double width = 640, double height = 480)
    {
        window.Title = "Memory Studio · " + title; window.Width = width; window.Height = height;
        window.MinWidth = 400; window.MinHeight = 300; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.SetResourceReference(Window.BackgroundProperty, "BackgroundBrush"); window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); window.FontSize = 12; window.UseLayoutRounding = true;
        window.SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(window);
    }
    public static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 5), TextWrapping = TextWrapping.Wrap };
    public static TextBox Input(string text = "") => new() { Text = text, Margin = new Thickness(0, 0, 0, 7), FontFamily = new("Consolas") };
    public static Button Button(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        button.Click += handler; return button;
    }
}
