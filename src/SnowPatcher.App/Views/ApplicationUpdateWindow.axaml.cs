using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SnowPatcher.App.ViewModels;
using SnowPatcher.Core;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.Views;

[SuppressMessage("Design", "CA1001", Justification = "The Closed handler disposes the cancellation source.")]
public partial class ApplicationUpdateWindow : Window
{
    private readonly MainWindowViewModel _main;
    private readonly Func<PreparedApplicationUpdate, Task> _startHelper;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;
    private bool _handingOff;
    private bool _cancelWhenFinished;
    private bool _finished;

    public ApplicationUpdateWindow() : this(new MainWindowViewModel()) { }

    public ApplicationUpdateWindow(MainWindowViewModel main, Func<PreparedApplicationUpdate, Task>? startHelper = null)
    {
        _main = main;
        _startHelper = startHelper ?? (update => ApplicationUpdateService.StartHelperAsync(update));
        InitializeComponent();
        _main.PropertyChanged += OnMainChanged;
        Opened += async (_, _) =>
        {
            Refresh();
            await _main.CheckApplicationUpdatesAsync();
            UpdateButton.Focus();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _main.PropertyChanged -= OnMainChanged;
            _cancellation.Dispose();
        };
        Refresh();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        var installing = _main.HasModInstallRequest;
        var required = _main.RequiredSnowPatcherVersion is { } minimum && SnowPatcherVersion.Compare(SnowPatcherVersion.Current, minimum) < 0;
        Title = required ? "SnowPatcher update required" : "Update SnowPatcher";
        var names = _main.RequestedModNames;
        IntroductionText.Text = required
            ? $"{(names.Length == 0 ? "This mod" : names)} {(_main.RequestedModCount > 1 ? "require" : "requires")} SnowPatcher {_main.RequiredSnowPatcherVersion} or newer."
            : _main.AvailableApplicationVersion is { } version ? $"SnowPatcher {version} is available." : "SnowPatcher " + SnowPatcherVersion.Current;
        ExplanationText.Text = installing ? _main.RequestedModCount > 1 ? "SnowPatcher will restart, then install the mods." : "SnowPatcher will restart, then install the mod."
            : "SnowPatcher will restart after the update. Mods and backups are kept.";
        ProgressPanel.IsVisible = _busy;
        ProgressText.Text = _handingOff ? "Restarting SnowPatcher..." : _main.ApplicationUpdatePercent >= 0
            ? $"Downloading SnowPatcher {_main.AvailableApplicationVersion}... {_main.ApplicationUpdatePercent}%" : _main.ApplicationUpdateText;
        DownloadProgress.IsIndeterminate = _main.ApplicationUpdatePercent < 0;
        DownloadProgress.Value = Math.Max(0, _main.ApplicationUpdatePercent);
        CancelButton.IsEnabled = !_handingOff && !_cancelWhenFinished;
        UpdateButton.IsEnabled = !_busy && !_main.IsCheckingApplicationUpdate &&
            (!_main.HasSuitableApplicationUpdate || _main.CanUpdateApplication);
        UpdateButton.Content = _busy ? "Updating..." : _main.HasSuitableApplicationUpdate
            ? installing ? "Update & install" : "Update & restart" : "Check again";
        var error = _main.ApplicationUpdateError;
        var feedback = error.Length > 0 ? error : _busy ? "" : _main.IsCheckingApplicationUpdate ? "Checking for updates..."
            : _main.HasSuitableApplicationUpdate ? "" : installing
                ? "No compatible update is available yet. Check again later."
                : "SnowPatcher is up to date.";
        FeedbackText.Text = feedback;
        FeedbackText.IsVisible = feedback.Length > 0;
        FeedbackText.Foreground = error.Length > 0 ? Brushes.DarkRed : IntroductionText.Foreground;
    }

    private async void OnUpdate(object? sender, RoutedEventArgs args)
    {
        if (_busy)
        {
            return;
        }

        if (!_main.HasSuitableApplicationUpdate)
        {
            await _main.CheckApplicationUpdatesAsync();
            return;
        }

        if (_main.ApplicationUpdateError.Length > 0)
        {
            await _main.CheckApplicationUpdatesAsync();
            if (_main.ApplicationUpdateError.Length > 0 || !_main.HasSuitableApplicationUpdate)
            {
                return;
            }
        }

        _busy = true;
        Refresh();
        try
        {
            var update = await _main.PrepareApplicationUpdateAsync(_cancellation.Token);
            if (update is null)
            {
                return;
            }

            _handingOff = true;
            Refresh();
            await _startHelper(update);
            _finished = true;
            Close(true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            _main.CancelApplicationUpdate("Could not restart SnowPatcher. " + error.Message);
        }
        finally
        {
            _busy = false;
            _handingOff = false;
            if (!_finished)
            {
                Refresh();
                if (_cancelWhenFinished)
                {
                    Close();
                }
            }
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close();

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_finished)
        {
            return;
        }

        if (_busy)
        {
            args.Cancel = true;
            if (!_handingOff)
            {
                _cancelWhenFinished = true;
                _cancellation.Cancel();
                ProgressText.Text = "Cancelling...";
                CancelButton.IsEnabled = false;
            }
            return;
        }

        try
        { _main.DismissRequiredModInstallation(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            args.Cancel = true;
            _main.CancelApplicationUpdate("Could not cancel the saved mod installation. " + error.Message);
        }
    }
}
