using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SnowPatcher.App.ViewModels;

namespace SnowPatcher.App.Views;

public partial class ResumeModInstallWindow : Window
{
    private readonly MainWindowViewModel _main;
    private bool _busy;

    public ResumeModInstallWindow() : this(new MainWindowViewModel()) { }

    public ResumeModInstallWindow(MainWindowViewModel main)
    {
        _main = main;
        InitializeComponent();
        _main.PropertyChanged += OnMainChanged;
        Opened += async (_, _) => await InstallAsync();
        Closing += (_, args) => args.Cancel = _busy;
        Closed += (_, _) => _main.PropertyChanged -= OnMainChanged;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.PendingInstallationText))
        {
            MessageText.Text = _main.PendingInstallationText;
        }
    }

    private async Task InstallAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        CloseButton.IsEnabled = false;
        RetryButton.IsVisible = false;
        InstallProgress.IsVisible = true;
        MessageText.Text = "Installing mod...";
        var success = await _main.ResumeModInstallationAsync();
        _busy = false;
        InstallProgress.IsVisible = false;
        MessageText.Text = _main.PendingInstallationText;
        Title = success ? "Mod installed" : "Installation paused";
        CloseButton.Content = success ? "Done" : "Close";
        CloseButton.IsEnabled = true;
        RetryButton.IsVisible = !success && _main.CanResumeModInstallation;
        CloseButton.Focus();
    }

    private async void OnRetry(object? sender, RoutedEventArgs args) => await InstallAsync();
    private void OnClose(object? sender, RoutedEventArgs args) => Close();
}
