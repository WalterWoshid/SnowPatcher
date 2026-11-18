using SnowPatcher.App.Services;
using SnowPatcher.Core;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _automaticWork;
    private Task? _automaticTask;
    private bool _automaticActionsPaused;
    public bool AutomaticActionsPaused
    {
        get => _automaticActionsPaused;
        set { _automaticActionsPaused = value; RaisePropertyChanged(); }
    }
    public bool IsAutomaticWork => _automaticWork;
    public bool IsReadyForMaintenance => _libraryReady && !_automaticWork && !_isApplying && !_isRestoring && !_isBackingUp && !_isCheckingCompatibility &&
        !_updatingApplication && !_pendingInstallationRunning && !IsClosing && _archiveImportLock.CurrentCount > 0 && !Mods.Any(m => m.IsBusy);
    public bool GameNeedsReapply => _patchCheck?.GameNeedsReapply == true;
    public bool MatchesAppliedSelection => _patchCheck?.MatchesAppliedSelection == true;
    public bool HasPendingGameChanges => _patchCheck?.HasPendingChanges == true;
    public bool NeedsManualRecovery => _patchCheck?.NeedsRecovery == true;
    public string? AutomaticBlockReason => _patchCheck?.BlockReason;
    public bool IsGameRunning => GameFolder.Length > 0 && GameSessionGuard.GetRestoreBlockReason(new GameInstallation(GameFolder)) is not null;

    public Task<AutomaticModResult> UpdateModsAutomaticallyAsync()
    {
        if (!IsReadyForMaintenance || GameFolder.Length == 0 || IsGameRunning || !MatchesAppliedSelection || !CurrentPreferences.AutomaticallyUpdateMods)
        {
            return Task.FromResult(new AutomaticModResult(0));
        }

        var task = UpdateModsAutomaticallyCoreAsync();
        _automaticTask = task;
        return task;
    }

    private async Task<AutomaticModResult> UpdateModsAutomaticallyCoreAsync()
    {
        var candidates = Mods.Where(m => m.IsEnabled && m.HasUpdate && m.Source is not null).ToArray();
        if (candidates.Length == 0)
        {
            return new AutomaticModResult(0);
        }

        SetAutomaticWork(true);
        try
        {
            var token = _archiveImportCancellation.Token;
            var downloaded = new Dictionary<ModItemViewModel, DownloadedMod>();
            await _archiveImportLock.WaitAsync(token);
            try
            {
                foreach (var mod in candidates)
                {
                    if (!CurrentPreferences.AutomaticallyUpdateMods || AutomaticActionsPaused)
                    {
                        return new AutomaticModResult(0);
                    }

                    var release = await _modio.GetLatestAsync(mod.Source!.ModId, token);
                    mod.SetChecked(release);
                    if (!mod.HasUpdate)
                    {
                        continue;
                    }
                    downloaded[mod] = await _library.DownloadAsync(_modio, release, null, token);
                    if (downloaded[mod].Archive.Package?.Manifest.Id != mod.ToRecord().PackageId)
                    {
                        throw new InvalidDataException("A mod update has a different package ID. The current mods were kept.");
                    }
                }

                if (downloaded.Count == 0)
                {
                    SaveLibrary();
                    return new AutomaticModResult(0);
                }

                var inputs = Mods.Where(m => m.IsEnabled).Select(m => new CompatibilityInput(m.ToRecord().Id,
                    downloaded.TryGetValue(m, out var update) ? update.Archive : m.Archive)).ToArray();
                var check = await RunGameOperationAsync(() => _patchInstaller.CheckAsync(new GameInstallation(GameFolder), inputs, token), token);
                if (check.NeedsRecovery || check.Report.HasBlockingIssues || check.BlockReason is not null)
                {
                    var detail = check.Report.Mods.FirstOrDefault(m => m.HasBlockingIssues)?.Detail ?? check.BlockReason ?? "A mod update conflicts with the current selection.";
                    foreach (var mod in downloaded.Keys)
                    {
                        mod.SetCheckFailure("Automatic update skipped. " + detail);
                    }
                    return new AutomaticModResult(0, detail);
                }

                if (!CurrentPreferences.AutomaticallyUpdateMods || AutomaticActionsPaused || IsGameRunning)
                {
                    return new AutomaticModResult(0);
                }

                var archives = downloaded.Values.Select(d => new DeferredModArchive(d.Archive.SourcePath, d.Archive.Sha256,
                    d.Archive.DisplayName, d.Archive.Package?.Manifest.MinSnowPatcherVersion ?? Core.SnowPatcherVersion.Current, d.Source)).ToArray();
                var fingerprint = PendingSelectionFingerprint();
                _applicationUpdates.PendingMods.Save(GameFolder, archives, automatic: true, selectionFingerprint: fingerprint);
                _acceptedInstallation = new PendingModInstallation(GameFolder, archives, Automatic: true, SelectionFingerprint: fingerprint);
            }
            finally { _archiveImportLock.Release(); }

            return await ResumeModInstallationAsync() ? new AutomaticModResult(downloaded.Count) : new AutomaticModResult(0, PendingInstallationText);
        }
        catch (OperationCanceledException) when (_archiveImportCancellation.IsCancellationRequested) { return new AutomaticModResult(0); }
        catch (Exception error) when (IsApplicationUpdateError(error))
        {
            NoticeApplicationRequirement(error);
            return new AutomaticModResult(0, error.Message);
        }
        finally { SetAutomaticWork(false); }
    }

    public Task<bool> ReapplyModsAutomaticallyAsync()
    {
        if (AutomaticActionsPaused || CurrentPreferences.GameUpdates != Core.Settings.GameUpdateBehavior.ReapplyMods ||
            !IsReadyForMaintenance || !MatchesAppliedSelection || !GameNeedsReapply || NeedsManualRecovery || !CanApplySelection || IsGameRunning)
        {
            return Task.FromResult(false);
        }

        var task = ReapplyModsAutomaticallyCoreAsync();
        _automaticTask = task;
        return task;
    }

    private async Task<bool> ReapplyModsAutomaticallyCoreAsync()
    {
        SetAutomaticWork(true);
        try
        {
            _applyTask = ApplyCoreAsync(automatic: true);
            await _applyTask;
            return _patchCheck is { HasPendingChanges: false, BlockReason: null, Report.HasBlockingIssues: false };
        }
        finally { SetAutomaticWork(false); }
    }

    private void SetAutomaticWork(bool working)
    {
        _automaticWork = working;
        RaisePropertyChanged(nameof(IsAutomaticWork));
        RefreshApplyActions();
    }
}
