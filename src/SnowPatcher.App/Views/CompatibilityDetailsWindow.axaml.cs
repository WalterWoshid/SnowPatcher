using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SnowPatcher.App.Views;

public partial class CompatibilityDetailsWindow : Window
{
    public CompatibilityDetailsWindow() : this("", "") { }
    public CompatibilityDetailsWindow(string name, string details)
    {
        InitializeComponent();
        ModName.Text = name;
        Details.Text = details;
    }
    private void OnClose(object? sender, RoutedEventArgs args) => Close();
}
