using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;

namespace SnowPatcher.App.ViewModels;

public sealed class ModItemViewModel : ObservableObject
{
    private bool _isEnabled;
    private LibraryMod _record;
    private string _error = "";
    private string _busyText = "";
    private string _checkStatus = "Not checked";
    private string _checkDetail = "Run Check mods to inspect the enabled selection.";
    private string _unavailableReason;
    private string? _requiredSnowPatcherVersion;

    public ModItemViewModel(ModArchive archive, LibraryMod? saved = null, bool unavailable = false, string unavailableReason = "", string? requiredSnowPatcherVersion = null)
    {
        Archive = archive;
        _record = saved is null ? new LibraryMod(Guid.NewGuid(), archive.SourcePath, archive.Sha256, true, null, null, null, [], archive.Package?.Manifest.Id)
            : saved with
            {
                PackageId = saved.PackageId ?? archive.Package?.Manifest.Id
            };
        IsUnavailable = unavailable;
        _unavailableReason = unavailableReason;
        _requiredSnowPatcherVersion = requiredSnowPatcherVersion;
        if (unavailableReason.Length > 0)
        {
            SetCompatibilityState("Unavailable", unavailableReason);
        }
        _isEnabled = _record.IsEnabled;
        _record = _record with
        {
            ModioUrl = _record.ModioUrl ?? archive.Package?.Manifest.ModioUrl,
            DisplayName = _record.DisplayName ?? _record.Source?.Name ?? archive.DisplayName
        };
    }

    public ModArchive Archive
    { get; private set; }
    public string Name => Source?.Name ?? Archive.Package?.Manifest.Name ?? _record.DisplayName ?? Archive.DisplayName;
    public string SourcePath => Archive.SourcePath;
    public string Version => IsUnavailable ? "Unknown" : Archive.Package?.Manifest.Version ?? Source?.Version ?? "Unknown";
    public string VersionDetail => (Version == "Unknown" ? "This ZIP does not provide a verified mod version." : Version) +
        (Archive.Package?.Manifest.MinSnowPatcherVersion is { } minimum ? $"\nRequires SnowPatcher {minimum} or newer." : "") +
        (HasPreviousVersions ? "\nClick to choose a previous version." : "");
    public bool HasPreviousVersions => _record.PackageId != BuiltinMods.IndicatorId && _record.PreviousDownloads.Length > 0;
    public bool ShowPlainVersion => !HasPreviousVersions;
    public bool CanChooseVersion => HasPreviousVersions && !IsBusy;
    public string PreviousVersionsLabel => $"Previous versions of {Name}";
    public string Status => IsEnabled ? "Enabled" : "Disabled";
    public string ToggleLabel => $"{(IsEnabled ? "Disable" : "Enable")} {Name}";
    public string RemoveLabel => $"Remove {Name}";
    public string Details => $"{Name}" + (Archive.Package is { } package ? "\nBy " + string.Join(", ", package.Manifest.Authors) : "") +
        $"\n{(IsUnavailable ? _unavailableReason.Length > 0 ? _unavailableReason : "ZIP unavailable" : Archive.Package is null ? "Manual ZIP" : "Files checked")}\n{SourcePath}" +
        (ModPage is { } page ? "\n" + page.AbsoluteUri : "");
    public Uri? ModPage => _record.PackageId == BuiltinMods.IndicatorId ? null :
        ModioPage.Parse(Archive.Package?.Manifest.ModioUrl) ?? ModioPage.Parse(_record.ModioUrl) ??
        (Source is { ModId: > 0 } source ? ModioPage.ForId(source.ModId) : null);
    public bool HasModPage => ModPage is not null;
    public bool ShowPlainName => !HasModPage;
    public string ModPageLabel => $"Open mod.io page for {Name}";
    public ModioSource? Source => _record.Source;
    public DateTimeOffset? LastCheckedAt => _record.LastCheckedAt;
    public bool IsUnavailable
    { get; private set; }
    public bool IsBusy => _busyText.Length > 0;
    public bool ShowRemove => _record.PackageId != BuiltinMods.IndicatorId;
    public bool CanRemove => ShowRemove && !IsBusy;
    public bool CanToggle => !IsBusy && !IsUnavailable;
    public bool HasUpdate => Source is { } source && _record.Latest is { } latest && latest.FileId != source.FileId &&
        (latest.DateAdded > source.DateAdded || (latest.DateAdded == source.DateAdded && latest.FileId > source.FileId));
    public bool HasUpdateAction => Source is not null && !IsBusy &&
        (_error.Length > 0 || HasUpdate || (IsUnavailable && _record.Latest?.FileId == Source.FileId));
    public bool ShowUpdateStatus => !HasUpdateAction;
    public string UpdateActionText => _error.Length > 0 ? "Retry" : IsUnavailable ? "Download" : "Update";
    public string UpdateActionLabel => $"{UpdateActionText} {Name}";
    public string UpdateStatus => IsBusy ? _busyText : Source is null ? (IsUnavailable ? "ZIP unavailable" : "") :
        _record.Latest is null ? "Not checked" : _record.Latest.FileId == Source.FileId ? "Up to date" : "Older release";
    public string UpdateDetail => _error.Length > 0 ? _error : HasUpdate
        ? $"Version {_record.Latest!.Version} is available on mod.io." : _record.Latest is { } latest && Source is { } source && latest.FileId != source.FileId
            ? "mod.io's active release is older than your copy. It will not be installed automatically."
            : IsUnavailable ? "Download the live mod.io release again." : Source is null ? "This local ZIP is not connected to mod.io update checks."
                : $"Last checked {LastCheckedAt?.ToLocalTime():g}. Game compatibility is checked separately.";
    public bool NeedsRetry => _error.Length > 0;
    public string CheckStatus => IsEnabled ? _checkStatus : "Disabled";
    public string CheckDetail => IsEnabled ? _checkDetail : "Disabled mods are excluded from compatibility checks.";
    public string CheckLabel => $"Compatibility for {Name}: {CheckStatus}";

    public void SetCompatibilityState(string status, string detail)
    {
        _checkStatus = IsUnavailable && _unavailableReason.Length > 0 ? _requiredSnowPatcherVersion is null ? "Unavailable" : "Update required" : status;
        _checkDetail = IsUnavailable && _unavailableReason.Length > 0 ? _unavailableReason : detail;
        RaisePropertyChanged(nameof(CheckStatus));
        RaisePropertyChanged(nameof(CheckDetail));
        RaisePropertyChanged(nameof(CheckLabel));
    }

    public void SetCompatibility(ModCompatibility result, CompatibilityReport report)
    {
        SetCompatibilityState(result.Status, result.Detail + "\n\n" + report.BaselineDetail + $"\nChecked {report.CheckedAt.ToLocalTime():g}.");
    }

    public LibraryMod ToRecord() => _record with { IsEnabled = IsEnabled };

    public void SetBusy(string message)
    {
        _busyText = message;
        RefreshUpdates();
    }
    public void SetProgress(int percent)
    {
        if (!_busyText.StartsWith("Downloading", StringComparison.Ordinal))
        {
            return;
        }

        _busyText = $"Downloading {percent}%";
        RefreshUpdates();
    }
    public void SetCheckFailure(string error)
    {
        _error = error;
        RefreshUpdates();
    }

    public void SetChecked(ModioRelease release)
    {
        if (Source is not { } source || release.Source.ModId != source.ModId)
        {
            throw new InvalidDataException("mod.io returned a different mod.");
        }

        if (release.Source.FileId == source.FileId && release.Source.Md5 != source.Md5)
        {
            throw new InvalidDataException("The published file's checksum changed. The current mod was kept.");
        }

        _record = _record with
        {
            Latest = release.Source,
            LastCheckedAt = DateTimeOffset.UtcNow
        };
        _error = "";
        RefreshUpdates();
    }

    public void UseArchive(ModArchive archive, LibraryMod record)
    {
        if (record.Id != _record.Id || record.Sha256 != archive.Sha256 || record.SourcePath != archive.SourcePath ||
            (_record.PackageId is { } currentId && archive.Package?.Manifest.Id != currentId))
        {
            throw new InvalidDataException("The update has a different package ID. The current mod was kept.");
        }

        Archive = archive;
        _record = record;
        _error = "";
        IsUnavailable = false;
        _unavailableReason = "";
        _requiredSnowPatcherVersion = null;
        foreach (var name in new[] { nameof(Archive), nameof(Name), nameof(SourcePath), nameof(Source), nameof(Version),
                     nameof(VersionDetail), nameof(Details), nameof(ToggleLabel), nameof(RemoveLabel), nameof(ShowRemove),
                     nameof(HasPreviousVersions), nameof(ShowPlainVersion), nameof(PreviousVersionsLabel), nameof(CanChooseVersion),
                     nameof(ModPage), nameof(HasModPage), nameof(ShowPlainName), nameof(ModPageLabel) })
        {
            RaisePropertyChanged(name);
        }

        RefreshUpdates();
    }

    private void RefreshUpdates()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanRemove), nameof(CanToggle), nameof(HasUpdate), nameof(HasUpdateAction),
                     nameof(ShowUpdateStatus), nameof(UpdateActionText), nameof(UpdateActionLabel), nameof(UpdateStatus), nameof(UpdateDetail),
                     nameof(CanChooseVersion) })
        {
            RaisePropertyChanged(name);
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(Status));
            RaisePropertyChanged(nameof(ToggleLabel));
            RaisePropertyChanged(nameof(CheckStatus));
            RaisePropertyChanged(nameof(CheckDetail));
            RaisePropertyChanged(nameof(CheckLabel));
        }
    }
}
