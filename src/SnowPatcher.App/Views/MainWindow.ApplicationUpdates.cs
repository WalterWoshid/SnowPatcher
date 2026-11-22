using Avalonia.Interactivity;
using Avalonia.Threading;
using SnowPatcher.App.Services;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.Views;

public partial class MainWindow
{
    private bool _readyForUpdatePrompts;
    private bool _updatePromptQueued;
    private bool _showingUpdateDialog;

    public Task InitializeWindowAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        await ViewModel.LoadPendingModInstallationAsync();
        var folder = ViewModel.PendingGameFolder ?? ViewModel.CurrentPreferences.GameFolder;
        await Task.WhenAll(folder is not null ? ViewModel.SelectGameFolderAsync(folder) : ViewModel.AutoDetectGameFolderAsync(),
            ViewModel.InitializeLibraryAsync(), ViewModel.CurrentPreferences.CheckApplicationUpdates ? ViewModel.CheckApplicationUpdatesAsync() : Task.CompletedTask);
        if (ViewModel.GameFolder.Length == 0 && folder is not null)
        {
            await ViewModel.AutoDetectGameFolderAsync();
        }
        await ViewModel.CheckCompatibilityAsync();
        _readyForUpdatePrompts = true;
        if (ViewModel.IsClosing)
        {
            return;
        }

        _updateTimer.Start();
        _selectionTimer.Start();
        await PresentPendingDialogsAsync();
        await RunMaintenanceAsync();
    }

    public async Task PresentPendingDialogsAsync()
    {
        if (!_readyForUpdatePrompts || _showingUpdateDialog || _maintenanceRunning || IsInBackground || ViewModel.IsClosing || OwnedWindows.Count > 0)
        {
            return;
        }
        if (ViewModel.CanResumeModInstallation)
        {
            _showingUpdateDialog = true;
            try
            { await new ResumeModInstallWindow(ViewModel).ShowDialog(this); }
            finally { _showingUpdateDialog = false; }
        }
        else if (ViewModel.HasModInstallRequest && ViewModel.NeedsNewerApplication)
        {
            await ShowApplicationUpdateAsync();
        }
    }

    private async Task RunMaintenanceAsync()
    {
        if (_maintenanceRunning || !_readyForUpdatePrompts || ViewModel.IsClosing || OwnedWindows.Count > 0)
        {
            return;
        }
        _maintenanceRunning = true;
        try
        {
            var result = await _maintenance.RunAsync(IsInBackground, ViewModel.AutomaticActionsPaused);
            if (ViewModel.IsClosing)
            {
                return;
            }
            foreach (var notice in result.Notices)
            {
                Session?.Notify(notice);
            }
            if (result.ShowApplication)
            {
                Session?.Show();
            }
            if (result.ApplicationUpdate is { } update)
            {
                try
                {
                    if (!IsInBackground || OwnedWindows.Count > 0 || ViewModel.AutomaticActionsPaused ||
                        !ViewModel.CurrentPreferences.AutomaticallyUpdateApplication || ViewModel.IsGameRunning)
                    {
                        ViewModel.PostponeApplicationUpdate(update);
                        return;
                    }
                    await ApplicationUpdateService.StartHelperAsync(update);
                    await CloseForUpdateAsync();
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    ViewModel.CancelApplicationUpdate(error.Message);
                    Session?.Notify(new UserNotice("restart-failed:" + error.Message, "SnowPatcher could not restart", error.Message, true));
                }
            }
        }
        finally { _maintenanceRunning = false; }
    }

    private async Task CloseForUpdateAsync()
    {
        _forceExit = true;
        await ViewModel.CloseAsync();
        _closeApproved = true;
        Close();
    }

    private void OnApplicationUpdateRequested(object? sender, EventArgs args)
    {
        if (IsInBackground || _maintenanceRunning)
        {
            Session?.Notify(new UserNotice("requires-app:" + ViewModel.RequiredSnowPatcherVersion + ViewModel.RequestedModNames,
                "A mod needs a newer SnowPatcher", "Open SnowPatcher to finish installing " + ViewModel.RequestedModNames + ".", true));
            return;
        }
        if (!_readyForUpdatePrompts || _updatePromptQueued || _showingUpdateDialog || ViewModel.IsClosing)
        {
            return;
        }

        _updatePromptQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _updatePromptQueued = false;
            if (ViewModel.HasModInstallRequest)
            {
                await ShowApplicationUpdateAsync();
            }
        }, DispatcherPriority.Background);
    }

    private async void OnApplicationUpdate(object? sender, RoutedEventArgs args) => await ShowApplicationUpdateAsync();

    private async Task ShowApplicationUpdateAsync()
    {
        if (_showingUpdateDialog || ViewModel.IsClosing)
        {
            return;
        }

        _showingUpdateDialog = true;
        try
        {
            if (await new ApplicationUpdateWindow(ViewModel).ShowDialog<bool>(this))
            {
                await CloseForUpdateAsync();
            }
        }
        finally { _showingUpdateDialog = false; }
    }
}
