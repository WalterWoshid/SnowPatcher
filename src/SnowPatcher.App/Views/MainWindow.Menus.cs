using Avalonia.Interactivity;

namespace SnowPatcher.App.Views;

public partial class MainWindow
{
    private async void OnPreferences(object? sender, RoutedEventArgs args)
        => await ShowPreferencesAsync();

    public async Task ShowPreferencesAsync()
    {
        if (ViewModel.Preferences is { } preferences)
        {
            await new PreferencesWindow(preferences).ShowDialog(this);
        }
    }

    private async void OnCheckModUpdates(object? sender, RoutedEventArgs args) => await ViewModel.CheckUpdatesAsync(force: true);

    private async void OnProjectPage(object? sender, RoutedEventArgs args)
    {
        try
        { await Launcher.LaunchUriAsync(new Uri("https://github.com/WalterWoshid/SnowPatcher")); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ViewModel.SetStatus("Could not open GitHub. " + error.Message);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs args) => ExitApplication();
}
