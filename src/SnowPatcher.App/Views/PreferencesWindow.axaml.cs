using Avalonia.Controls;
using Avalonia.Interactivity;
using SnowPatcher.App.Services;
using SnowPatcher.App.ViewModels;

namespace SnowPatcher.App.Views;

public partial class PreferencesWindow : Window
{
    private readonly PreferencesService _preferences;
    public PreferencesWindow() : this(new PreferencesService()) { }
    public PreferencesWindow(PreferencesService preferences)
    {
        _preferences = preferences;
        InitializeComponent();
        DataContext = new PreferencesViewModel(preferences.Current, preferences.SupportsStartup);
    }

    private void OnSave(object? sender, RoutedEventArgs args)
    {
        try
        {
            _preferences.Save(((PreferencesViewModel)DataContext!).Result with { GameFolder = _preferences.Current.GameFolder });
            Close(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            ErrorText.Text = "Could not save preferences. " + error.Message;
            ErrorText.IsVisible = true;
        }
    }
    private void OnDefaults(object? sender, RoutedEventArgs args) => ((PreferencesViewModel)DataContext!).Reset();
    private void OnCancel(object? sender, RoutedEventArgs args) => Close(false);
}
