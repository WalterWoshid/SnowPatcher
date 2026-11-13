using SnowPatcher.Core.Backups;

namespace SnowPatcher.App.ViewModels;

public sealed class BackupItemViewModel(ArchiveBackup backup, bool canRename) : ObservableObject
{
    private string _draftName = "";
    private bool _isEditing;

    public ArchiveBackup Backup { get; private set; } = backup;
    public string Name => Backup.Label ?? Backup.DisplayName;
    public string Details => Backup.Details;
    public bool HasName => Backup.Label is not null;
    public string AutomaticName => $"{Backup.CreatedAt.ToLocalTime():g} - {Backup.DefaultName}";
    public string RenameLabel => $"Rename backup: {Backup.DisplayName}";
    public bool CanRename { get; } = canRename;
    public bool IsEditing => _isEditing;
    public bool ShowName => !_isEditing;
    public bool ShowRename => CanRename && !_isEditing;

    public string DraftName
    {
        get => _draftName;
        set
        {
            value ??= "";
            if (_draftName == value)
            {
                return;
            }

            _draftName = value;
            RaisePropertyChanged();
        }
    }

    public void BeginEdit()
    {
        DraftName = Backup.Label ?? "";
        SetEditing(true);
    }

    public void CancelEdit()
    {
        DraftName = Backup.Label ?? "";
        SetEditing(false);
    }

    public void UpdateBackup(ArchiveBackup updated)
    {
        Backup = updated;
        foreach (var property in new[] { nameof(Backup), nameof(Name), nameof(Details), nameof(HasName), nameof(AutomaticName), nameof(RenameLabel) })
        {
            RaisePropertyChanged(property);
        }

        CancelEdit();
    }

    private void SetEditing(bool value)
    {
        _isEditing = value;
        RaisePropertyChanged(nameof(IsEditing));
        RaisePropertyChanged(nameof(ShowName));
        RaisePropertyChanged(nameof(ShowRename));
    }
}
