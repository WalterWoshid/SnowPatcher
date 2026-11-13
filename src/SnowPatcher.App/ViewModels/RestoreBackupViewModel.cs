using System.Collections.ObjectModel;
using SnowPatcher.Core;
using SnowPatcher.Core.Backups;

namespace SnowPatcher.App.ViewModels;

public sealed class RestoreBackupViewModel : ObservableObject
{
    private readonly GameInstallation _installation;
    private BackupItemViewModel? _selectedBackup;
    private BackupItemViewModel? _editingBackup;
    private string _message = "";
    private string? _blockReason;
    private bool _isBusy;

    public RestoreBackupViewModel(GameInstallation installation, IReadOnlyList<ArchiveBackup> backups, bool canRename = true)
    {
        _installation = installation;
        Backups = new(backups.Select(b => new BackupItemViewModel(b, canRename)));
        _selectedBackup = Backups.FirstOrDefault();
    }

    public ObservableCollection<BackupItemViewModel> Backups
    { get; }
    public string Message => _message;
    public string? BlockReason => _blockReason;
    public bool IsBusy => _isBusy;
    public bool CanSelect => !_isBusy;
    public bool CanRestore => !_isBusy && EditingBackup is null && SelectedBackup is not null && _blockReason is null;
    public BackupItemViewModel? EditingBackup => _editingBackup;

    public BackupItemViewModel? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (_selectedBackup == value)
            {
                return;
            }

            if (_editingBackup is not null && _editingBackup != value)
            {
                CancelRename();
            }

            _selectedBackup = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanRestore));
            SetMessage("");
        }
    }

    public void BeginRename(BackupItemViewModel backup)
    {
        if (_isBusy || !backup.CanRename || !Backups.Contains(backup))
        {
            return;
        }

        CancelRename();
        SelectedBackup = backup;
        _editingBackup = backup;
        backup.BeginEdit();
        RefreshEditing();
    }

    public void CancelRename()
    {
        if (_isBusy)
        {
            return;
        }

        _editingBackup?.CancelEdit();
        _editingBackup = null;
        RefreshEditing();
    }

    public void FinishRename(BackupItemViewModel row, ArchiveBackup updated)
    {
        row.UpdateBackup(updated);
        _editingBackup = null;
        SelectedBackup = row;
        RefreshEditing();
    }

    private void RefreshEditing()
    {
        RaisePropertyChanged(nameof(EditingBackup));
        RaisePropertyChanged(nameof(CanRestore));
        SetMessage("");
    }

    public void RefreshAvailability()
    {
        _blockReason = GameSessionGuard.GetRestoreBlockReason(_installation);
        RaisePropertyChanged(nameof(BlockReason));
        RaisePropertyChanged(nameof(CanRestore));
    }

    public void SetBusy(bool value)
    {
        _isBusy = value;
        RaisePropertyChanged(nameof(IsBusy));
        RaisePropertyChanged(nameof(CanSelect));
        RaisePropertyChanged(nameof(CanRestore));
    }

    public void SetMessage(string text)
    {
        _message = text;
        RaisePropertyChanged(nameof(Message));
    }
}
