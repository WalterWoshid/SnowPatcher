using System.Security.Cryptography;
using System.Text;
using SnowPatcher.Core;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly List<DeferredModArchive> _pendingModArchives = [];
    private string? _minimumRequiredVersion;
    private PendingModInstallation? _acceptedInstallation;
    private string _pendingInstallationText = "";
    private bool _pendingInstallationRunning;

    public event EventHandler? ApplicationUpdateRequested;
    public bool HasModInstallRequest => _minimumRequiredVersion is not null || _pendingModArchives.Count > 0;
    public string? RequiredSnowPatcherVersion => _minimumRequiredVersion;
    public string RequestedModNames => string.Join(", ", _pendingModArchives.Select(a => a.DisplayName).Distinct());
    public int RequestedModCount => _pendingModArchives.Count;
    public bool HasAcceptedModInstallation => _acceptedInstallation is { Automatic: false } accepted &&
        _pendingModArchives.All(a => accepted.Archives.Any(saved => saved.Sha256 == a.Sha256));
    public string? PendingGameFolder => _acceptedInstallation?.GameFolder;
    public bool NeedsNewerApplication => RequiredSnowPatcherVersion is { } minimum && SnowPatcherVersion.Compare(SnowPatcherVersion.Current, minimum) < 0;
    public bool CanResumeModInstallation => _acceptedInstallation is { } request &&
        (!request.Automatic || CurrentPreferences.AutomaticallyUpdateMods) &&
        request.Archives.All(a => SnowPatcherVersion.Compare(a.RequiredVersion, SnowPatcherVersion.Current) <= 0);
    public string PendingInstallationText => _pendingInstallationText;

    public async Task LoadPendingModInstallationAsync()
    {
        try
        {
            _acceptedInstallation = await Task.Run(_applicationUpdates.PendingMods.Read);
            foreach (var archive in _acceptedInstallation?.Archives ?? [])
            {
                RememberRequiredMod(archive.RequiredVersion, archive);
            }
        }
        catch (Exception error) when (IsApplicationUpdateError(error))
        {
            SetStatus("Could not resume the mod installation. " + error.Message);
        }
    }

    private void NoticeApplicationRequirement(Exception error, DeferredModArchive? archive = null)
    {
        if (error is SnowPatcherUpdateRequiredException requirement)
        {
            RememberRequiredMod(requirement.RequiredVersion, archive ?? requirement.Archive);
        }
    }

    private void RememberRequiredMod(string version, DeferredModArchive? archive)
    {
        if (_minimumRequiredVersion is null || SnowPatcherVersion.Compare(version, _minimumRequiredVersion) > 0)
        {
            _minimumRequiredVersion = version;
        }

        if (archive is not null && !_pendingModArchives.Any(a => a.Sha256 == archive.Sha256))
        {
            _pendingModArchives.Add(archive);
        }

        RaisePropertyChanged(nameof(HasModInstallRequest));
        RaisePropertyChanged(nameof(RequiredSnowPatcherVersion));
        RaisePropertyChanged(nameof(RequestedModNames));
        RaisePropertyChanged(nameof(RequestedModCount));
        RefreshApplicationUpdate();
    }

    private void RequestApplicationUpdateDialog()
    {
        if (HasModInstallRequest && !IsClosing)
        {
            ApplicationUpdateRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DismissRequiredModInstallation()
    {
        _applicationUpdates.PendingMods.Complete();
        _pendingModArchives.Clear();
        _minimumRequiredVersion = null;
        _acceptedInstallation = null;
        RefreshApplicationUpdate();
    }

    public async Task<bool> ResumeModInstallationAsync()
    {
        if (_acceptedInstallation is not { } request || _pendingInstallationRunning || IsClosing)
        {
            return false;
        }

        if (!CanResumeModInstallation)
        {
            SetPendingInstallationText("Update SnowPatcher before installing this mod.");
            return false;
        }

        _pendingInstallationRunning = true;
        RefreshApplyActions();
        try
        {
            if (request.Automatic && request.SelectionFingerprint != PendingSelectionFingerprint())
            {
                CompletePendingInstallation();
                SetPendingInstallationText("Automatic installation stopped because your mod selection changed. Review the selection before updating again.");
                return false;
            }
            var token = _archiveImportCancellation.Token;
            await _archiveImportLock.WaitAsync(token);
            try
            {
                foreach (var deferred in request.Archives)
                {
                    SetPendingInstallationText("Adding " + deferred.DisplayName + "...");
                    var archive = await _library.ImportAsync(deferred.SourcePath, deferred.Sha256, token);
                    await AcceptArchiveAsync(archive, deferred.Source, token);
                }
            }
            finally { _archiveImportLock.Release(); }

            if (request.Automatic)
            {
                request = request with { SelectionFingerprint = PendingSelectionFingerprint() };
                _applicationUpdates.PendingMods.Save(request.GameFolder, request.Archives, automatic: true, selectionFingerprint: request.SelectionFingerprint);
                _acceptedInstallation = request;
            }

            if (GameFolder.Length == 0)
            {
                SetPendingInstallationText("The mod was added. Choose your SnowRunner folder, then click Apply.");
                return false;
            }

            SetPendingInstallationText("Checking game files and mod compatibility...");
            await StartCompatibilityAsync(false);
            if (request.Automatic && (!CurrentPreferences.AutomaticallyUpdateMods))
            {
                SetPendingInstallationText("Automatic installation is paused. Click Apply to finish manually, or enable automatic updates again.");
                return false;
            }
            if (CanApplySelection)
            {
                SetPendingInstallationText("Backing up game files and installing mods...");
                _applyTask = ApplyCoreAsync(automatic: false);
                await _applyTask;
            }

            if (IsPendingInstallationApplied(request))
            {
                CompletePendingInstallation();
                SetPendingInstallationText(request.Archives.Length == 1 ? request.Archives[0].DisplayName + " is installed." : "Mods installed.");
                return true;
            }

            SetPendingInstallationText(StatusText.Length > 0 ? StatusText : _patchCheck?.BlockReason ?? "The mod was added, but it could not be applied. Check the mod list for details.");
            return false;
        }
        catch (Exception error) when (IsApplicationUpdateError(error))
        {
            NoticeApplicationRequirement(error);
            SetPendingInstallationText("Could not install the mod. " + error.Message);
            return false;
        }
        finally { _pendingInstallationRunning = false; RefreshApplyActions(); RefreshCompatibilityActions(); }
    }

    private string PendingSelectionFingerprint()
    {
        var folder = Path.TrimEndingDirectorySeparator(GameFolder);
        folder = OperatingSystem.IsWindows() ? folder.ToUpperInvariant() : folder;
        var selection = folder + "\n" + string.Join('\n', Mods.Select(m => m.ToRecord()).OrderBy(m => m.Id)
            .Select(m => $"{m.Id}:{m.Sha256}:{m.IsEnabled}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection)));
    }

    private bool IsPendingInstallationApplied(PendingModInstallation request) => _patchCheck is { HasPendingChanges: false, BlockReason: null, NeedsRecovery: false, Report.HasIssues: false } &&
        request.Archives.All(a => Mods.Any(m => m.Archive.Sha256 == a.Sha256 && !m.IsUnavailable));

    private void CompletePendingInstallation()
    {
        _applicationUpdates.PendingMods.Complete();
        if (_acceptedInstallation is { } request)
        {
            var completed = request.Archives.Select(a => a.Sha256).ToHashSet(StringComparer.Ordinal);
            _pendingModArchives.RemoveAll(a => completed.Contains(a.Sha256));
        }
        _acceptedInstallation = null;
        _minimumRequiredVersion = _pendingModArchives.Select(a => a.RequiredVersion)
            .Order(Comparer<string>.Create(SnowPatcherVersion.Compare)).LastOrDefault();
    }

    private void SetPendingInstallationText(string message)
    {
        _pendingInstallationText = message;
        RaisePropertyChanged(nameof(PendingInstallationText));
    }
}
