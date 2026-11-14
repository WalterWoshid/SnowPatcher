using SnowPatcher.Core;
using SnowPatcher.Core.Installations;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task SelectGameFolderAsync(string path)
    {
        if (IsClosing)
        {
            return;
        }

        var installation = GameInstallationLocator.Resolve(path);
        if (!installation.HasInitialPak && !_patchInstaller.HasManagedState(installation))
        {
            SetStatus("That folder does not contain SnowRunner's initial.pak. Choose the SnowRunner installation folder.");
            return;
        }

        await SetGameFolderAsync(installation);
    }

    public async Task AutoDetectGameFolderAsync()
    {
        if (_isDetecting || GameFolder.Length > 0 || IsClosing)
        {
            return;
        }

        var selectionVersion = _folderSelectionVersion;
        _isDetecting = true;
        SetStatus("Looking for SnowRunner...");

        try
        {
            var installation = await Task.Run(() => _patchInstaller.FindRecoverableInstallation() ?? GameInstallationLocator.Find());
            // A folder chosen through Browse while detection runs takes precedence.
            if (_folderSelectionVersion != selectionVersion || IsClosing)
            {
                return;
            }

            if (installation is null)
            {
                SetStatus("SnowRunner wasn't found. Use Browse to select its folder.");
            }
            else
            {
                await SetGameFolderAsync(installation);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (_folderSelectionVersion == selectionVersion)
            {
                SetStatus("Couldn't locate SnowRunner. Use Browse to select its folder.");
            }
        }
        finally
        {
            _isDetecting = false;
        }
    }

    private async Task SetGameFolderAsync(GameInstallation installation)
    {
        if (IsClosing)
        {
            return;
        }

        _verificationCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_archiveImportCancellation.Token);
        _verificationCancellation = cancellation;
        _folderSelectionVersion++;
        var selectionVersion = _folderSelectionVersion;
        _compatibilityCancellation?.Cancel();
        _compatibilityVersion++;
        _isCheckingCompatibility = false;
        _patchCheck = null;
        Compatibility = null;
        SetCompatibilitySummary("");
        _gameFolder = installation.RootDirectory;
        try
        { _preferences?.RememberGameFolder(_gameFolder); }
        catch (Exception error) when (IsLibraryError(error)) { SetStatus("Could not remember the game folder. " + error.Message); }
        RaisePropertyChanged(nameof(GameFolder));
        RaisePropertyChanged(nameof(HasInstallationDetails));
        Inspection = null;
        _hasBackups = false;
        RefreshBackupActions();
        SetBuildText("Build: Checking...");
        SetStatus("");

        try
        {
            InstallationInspection result;
            await _gameCheckLock.WaitAsync(cancellation.Token);
            try
            {
                result = await Task.Run(() => GameInstallationVerifier.InspectAsync(installation, cancellation.Token), cancellation.Token);
                if (selectionVersion != _folderSelectionVersion || cancellation.IsCancellationRequested)
                {
                    return;
                }

                Inspection = result;
                ShowBuild(result);
                SetStatus(result.State == FileVerificationState.UnableToVerify ? result.Detail : "");

                if (result.BuildId is not null)
                {
                    _isBackingUp = true;
                    RefreshBackupActions();
                    SetStatus("Saving backup...");
                    try
                    {
                        await Task.Run(() => _backupStore.EnsureBackupAsync(installation, result.BuildId, cancellation.Token), cancellation.Token);
                        var backups = await Task.Run(() => _backupStore.ListAsync(installation, cancellation.Token), cancellation.Token);
                        if (selectionVersion != _folderSelectionVersion || cancellation.IsCancellationRequested)
                        {
                            return;
                        }

                        _hasBackups = backups.Count > 0;
                        SetStatus(result.State == FileVerificationState.UnableToVerify ? result.Detail : "");
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException)
                    {
                        SetStatus($"Could not save a backup: {error.Message}");
                    }
                    finally
                    {
                        _isBackingUp = false;
                        RefreshBackupActions();
                    }
                }
            }
            finally { _gameCheckLock.Release(); }

            RequestCompatibilityCheck();
            if (result.BuildId is not null && result.Store == GameStore.Steam)
            {
                var releasedAt = await SteamReleaseInfo.GetReleaseTimeAsync(result.BuildId, cancellation.Token);
                if (selectionVersion != _folderSelectionVersion || cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (releasedAt is not null && Inspection?.BuildId == result.BuildId && Inspection.Store == result.Store)
                {
                    Inspection = Inspection with
                    {
                        ReleasedAt = releasedAt
                    };
                    ShowBuild(Inspection);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_verificationCancellation, cancellation))
            {
                _verificationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public void CancelVerification() => _verificationCancellation?.Cancel();

    private void ShowBuild(InstallationInspection inspection)
    {
        var text = $"Build: {inspection.BuildId ?? "Unknown"}";
        if (inspection.Store == GameStore.Epic)
        {
            text += " (Epic Games)";
        }
        else if (inspection.Store == GameStore.Manual)
        {
            text += " (Manual installation)";
        }
        if (inspection.ReleasedAt is { } releasedAt)
        {
            text += $" ({releasedAt.ToLocalTime():g})";
        }

        SetBuildText(text, inspection.ReleasedAt is null ? "" : "Steam public update release date and time, shown in local time.");
    }

    private void SetBuildText(string build, string detail = "")
    {
        _buildText = build;
        _buildDetail = detail;
        RaisePropertyChanged(nameof(BuildText));
        RaisePropertyChanged(nameof(BuildDetail));
    }
}
