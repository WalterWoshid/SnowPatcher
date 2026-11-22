using SnowPatcher.Core;
using SnowPatcher.Core.Backups;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<IReadOnlyList<ArchiveBackup>> GetBackupsAsync()
    {
        var installation = new GameInstallation(GameFolder);
        return await Task.Run(() => _backupStore.ListAsync(installation));
    }

    public Task<ArchiveBackup> RenameBackupAsync(ArchiveBackup backup, string? name)
    {
        var installation = new GameInstallation(GameFolder);
        return Task.Run(() => _backupStore.RenameAsync(installation, backup, name));
    }

    public Task<BackupRestoreResult> RestoreBackupAsync(ArchiveBackup backup)
    {
        if (_restoreTask is { IsCompleted: false })
        {
            return _restoreTask;
        }
        if (!CanBrowse)
        {
            throw new InvalidOperationException("Wait for the current operation before restoring a backup.");
        }

        return _restoreTask = RestoreCoreAsync(backup);
    }

    private async Task<BackupRestoreResult> RestoreCoreAsync(ArchiveBackup backup)
    {
        CancelVerification();
        _compatibilityCancellation?.Cancel();
        _isRestoring = true;
        RefreshBackupActions();
        try
        {
            var installation = new GameInstallation(GameFolder);
            var result = await RunGameOperationAsync(() => Task.Run(() => _backupStore.RestoreAsync(installation, backup)));
            ClearQueuedSelection();
            return result;
        }
        finally
        {
            _isRestoring = false;
            RefreshBackupActions();
        }
    }

    private void RefreshBackupActions()
    {
        RaisePropertyChanged(nameof(CanBrowse));
        RaisePropertyChanged(nameof(CanRestore));
        RaisePropertyChanged(nameof(CanAddMods));
        RaisePropertyChanged(nameof(CanEditMods));
        RaisePropertyChanged(nameof(CanUpdateApplication));
        RefreshCompatibilityActions();
    }
}
