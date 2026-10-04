using System.Windows;

namespace MemoryStudio;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkTitleBar(this);
        Loaded += (_, _) => AppearanceSettings.Apply(this);
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        AppearanceSettings.Current.ScalePercent = 100;
        AppearanceSettings.Current.FontSize = 13;
        AppearanceSettings.Current.RememberWindowSize = true;
    }
    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
