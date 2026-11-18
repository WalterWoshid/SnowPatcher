using SnowPatcher.Core;
using SnowPatcher.Core.Compatibility;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Task CheckCompatibilityAsync() => _automaticWork ? Task.CompletedTask : StartCompatibilityAsync(false);

    private void RequestCompatibilityCheck() => _ = StartCompatibilityAsync(true);

    private async Task StartCompatibilityAsync(bool debounce)
    {
        if (IsClosing || (_automaticWork || _pendingInstallationRunning) && debounce)
        {
            return;
        }

        _compatibilityCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_archiveImportCancellation.Token);
        _compatibilityCancellation = cancellation;
        var version = ++_compatibilityVersion;
        var token = cancellation.Token;
        Compatibility = null;
        _patchCheck = null;
        _isCheckingCompatibility = false;
        SetCompatibilitySummary("");
        foreach (var mod in Mods)
        {
            mod.SetCompatibilityState("Not checked", "The selection changed. A new check is needed.");
        }

        RefreshCompatibilityActions();
        try
        {
            if (!CanCheckSelection)
            {
                return;
            }

            SetStatus("");
            if (debounce)
            {
                await Task.Delay(300, token);
            }

            token.ThrowIfCancellationRequested();
            _isCheckingCompatibility = true;
            RefreshCompatibilityActions();
            var selected = Mods.Where(m => m.IsEnabled).ToArray();
            foreach (var mod in selected)
            {
                mod.SetCompatibilityState("Checking...", "Checking game files and conflicts.");
            }

            SetCompatibilitySummary("Checking enabled mods...");
            var patchCheck = await RunGameOperationAsync(() => _patchInstaller.CheckAsync(new GameInstallation(GameFolder),
                selected.Select(m => new CompatibilityInput(m.ToRecord().Id, m.Archive)).ToArray(), token), token);
            if (version != _compatibilityVersion || token.IsCancellationRequested)
            {
                return;
            }

            _patchCheck = patchCheck;
            var report = patchCheck.Report;
            if (report.BuildId is { } build && (Inspection?.BuildId != build || Inspection.Store != report.Store))
            {
                Inspection = new InstallationInspection(build, report.InitialPakState, report.BaselineDetail, Store: report.Store);
                ShowBuild(Inspection);
            }
            Compatibility = report;
            foreach (var result in report.Mods)
            {
                selected.Single(m => m.ToRecord().Id == result.LibraryId).SetCompatibility(result, report);
            }

            var issues = report.Mods.Count(m => m.HasBlockingIssues);
            SetCompatibilitySummary(issues > 0 ? $"{issues} {(issues == 1 ? "mod needs" : "mods need")} attention. Click a check result for details."
                : patchCheck.BlockReason ?? (report.HasWarnings ? "Some translations could not be applied. Click a check result for details."
                    : patchCheck.HasPendingChanges ? "Changes not applied." : ""));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsLibraryError(error))
        {
            if (version != _compatibilityVersion)
            {
                return;
            }

            foreach (var mod in Mods.Where(m => m.IsEnabled))
            {
                mod.SetCompatibilityState("Needs review", error.Message);
            }

            SetCompatibilitySummary(error.Message);
        }
        finally
        {
            if (version == _compatibilityVersion)
            {
                _isCheckingCompatibility = false;
                RefreshCompatibilityActions();
            }
            if (ReferenceEquals(_compatibilityCancellation, cancellation))
            {
                _compatibilityCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void RefreshCompatibilityActions()
    {
        RaisePropertyChanged(nameof(CanCheckMods));
        RaisePropertyChanged(nameof(CheckModsText));
        RaisePropertyChanged(nameof(CanApply));
        RaisePropertyChanged(nameof(ApplyText));
        RaisePropertyChanged(nameof(ApplyDetail));
        RaisePropertyChanged(nameof(CanUpdateApplication));
    }

    private void SetCompatibilitySummary(string text)
    {
        _compatibilitySummary = text;
        RaisePropertyChanged(nameof(StatusText));
    }

    public Task ApplyModsAsync() => IsClosing || _automaticWork ? Task.CompletedTask :
        _applyTask is { IsCompleted: false } ? _applyTask : _applyTask = ApplyCoreAsync();

    private async Task ApplyCoreAsync(bool automatic = false)
    {
        if (!CanApplySelection || _automaticWork && !automatic)
        {
            return;
        }

        CancelVerification();
        _compatibilityCancellation?.Cancel();
        var selected = Mods.Where(m => m.IsEnabled).Select(m => new CompatibilityInput(m.ToRecord().Id, m.Archive)).ToArray();
        _isApplying = true;
        RefreshApplyActions();
        string message;
        try
        {
            var result = await RunGameOperationAsync(() => _patchInstaller.ApplyAsync(new GameInstallation(GameFolder), selected, new Progress<string>(SetStatus)));
            message = result.Recovered ? "Recovery completed." : result.Changed ? selected.Length == 0 ? "Mods removed." : "Mods applied." : "No changes to apply.";
            if (result.HasWarnings)
            {
                message += " Some translations were skipped. Click a check result for details.";
            }
            _hasBackups = (await _backupStore.ListAsync(new GameInstallation(GameFolder))).Count > 0;
        }
        catch (Exception error) when (IsLibraryError(error) || error is System.Text.Json.JsonException)
        {
            message = error.Message;
        }
        finally { _isApplying = false; RefreshApplyActions(); }
        await StartCompatibilityAsync(false);
        if (_acceptedInstallation is { } request && IsPendingInstallationApplied(request))
        {
            try
            { CompletePendingInstallation(); }
            catch (Exception error) when (IsLibraryError(error)) { message += " Could not clear the saved installation request. " + error.Message; }
        }
        SetStatus(message);
    }

    private void RefreshApplyActions()
    {
        RaisePropertyChanged(nameof(IsApplying));
        RaisePropertyChanged(nameof(CanEditMods));
        RaisePropertyChanged(nameof(CanAddMods));
        RaisePropertyChanged(nameof(CanUpdateApplication));
        RefreshBackupActions();
    }
}
