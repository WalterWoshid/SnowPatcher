using Avalonia.Interactivity;
using Avalonia.Threading;
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
        await PresentPendingDialogsAsync();
    }

    public async Task PresentPendingDialogsAsync()
    {
        if (!_readyForUpdatePrompts || _showingUpdateDialog || IsInBackground || ViewModel.IsClosing || OwnedWindows.Count > 0)
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



    private async Task CloseForUpdateAsync()
    {
        _forceExit = true;
        await ViewModel.CloseAsync();
        _closeApproved = true;
        Close();
    }

    private void OnApplicationUpdateRequested(object? sender, EventArgs args)
    {
        if (IsInBackground)
        {
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
