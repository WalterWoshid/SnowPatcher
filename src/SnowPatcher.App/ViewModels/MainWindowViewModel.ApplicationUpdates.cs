using System.Net.Http;
using System.Text.Json;
using SnowPatcher.Core;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly ApplicationUpdateService _applicationUpdates;
    private ApplicationRelease? _applicationRelease;
    private bool _checkingApplicationUpdate;
    private bool _updatingApplication;
    private string _applicationUpdateDetail = "Check for SnowPatcher updates.";
    private string _applicationUpdateProgress = "";
    private Task? _applicationCheckTask;
    private Task<PreparedApplicationUpdate?>? _applicationUpdateTask;
    private int _applicationUpdatePercent = -1;
    private string _applicationUpdateError = "";
    private bool _updateNoticeRead;
    private ApplicationUpdateNotice? _lastUpdateNotice;

    public ApplicationUpdateNotice? ReadApplicationUpdateNotice()
    {
        if (!_updateNoticeRead)
        {
            _lastUpdateNotice = _applicationUpdates.ReadLastNotice();
            _updateNoticeRead = true;
        }
        return _lastUpdateNotice;
    }

    public string ApplicationTitle { get; } = "SnowPatcher " + SnowPatcherVersion.Current;
    public string ApplicationUpdateText => _updatingApplication ? _applicationUpdateProgress : _checkingApplicationUpdate ? "Checking for updates..."
        : _applicationRelease is { } release ? "Update to " + release.Version : ApplicationTitle;
    public string ApplicationUpdateDetail => _applicationUpdateDetail;
    public bool HasApplicationUpdate => _applicationRelease is not null;
    public bool IsCheckingApplicationUpdate => _checkingApplicationUpdate;
    public bool IsUpdatingApplication => _updatingApplication;
    public int ApplicationUpdatePercent => _applicationUpdatePercent;
    public string ApplicationUpdateError => _applicationUpdateError;
    public string? AvailableApplicationVersion => _applicationRelease?.Version;
    public bool HasSuitableApplicationUpdate => _applicationRelease is { } release &&
        (RequiredSnowPatcherVersion is not { } minimum || SnowPatcherVersion.Compare(release.Version, minimum) >= 0);
    public bool CanUpdateApplication => !IsClosing && !_updatingApplication && !_checkingApplicationUpdate && !_pendingInstallationRunning && !_isApplying && !_isRestoring && !_isBackingUp &&
        !_isCheckingCompatibility && _archiveImportLock.CurrentCount > 0 && !Mods.Any(m => m.IsBusy);

    public Task CheckApplicationUpdatesAsync() => IsClosing || _updatingApplication ? Task.CompletedTask :
        _checkingApplicationUpdate ? _applicationCheckTask ?? Task.CompletedTask : _applicationCheckTask = CheckApplicationUpdatesCoreAsync();

    private async Task CheckApplicationUpdatesCoreAsync()
    {
        _checkingApplicationUpdate = true;
        _applicationUpdateError = "";
        RefreshApplicationUpdate();
        try
        {
            var notice = ReadApplicationUpdateNotice();
            _applicationRelease = await _applicationUpdates.CheckAsync(_archiveImportCancellation.Token);
            _applicationUpdateDetail = _applicationRelease is { } release
                ? $"Download SnowPatcher {release.Version} and restart. Mods and backups are kept."
                : notice?.Message ?? "SnowPatcher is up to date. Click to check again.";
        }
        catch (OperationCanceledException) when (_archiveImportCancellation.IsCancellationRequested) { }
        catch (Exception error) when (IsApplicationUpdateError(error))
        {
            _applicationUpdateError = "Could not check for updates. " + error.Message;
            _applicationUpdateDetail = "Could not check for SnowPatcher updates. Click to retry. " + error.Message;
        }
        finally
        {
            _checkingApplicationUpdate = false;
            RefreshApplicationUpdate();
        }
    }

    public Task<PreparedApplicationUpdate?> PrepareApplicationUpdateAsync(CancellationToken token = default) => PrepareApplicationUpdateAsync(false, token);

    public Task<PreparedApplicationUpdate?> PrepareApplicationUpdateAsync(bool restartInBackground, CancellationToken token = default) => !CanUpdateApplication || !HasSuitableApplicationUpdate || _applicationRelease is null
        ? Task.FromResult<PreparedApplicationUpdate?>(null)
        : _applicationUpdateTask = PrepareApplicationUpdateCoreAsync(_applicationRelease, restartInBackground, token);

    private async Task<PreparedApplicationUpdate?> PrepareApplicationUpdateCoreAsync(ApplicationRelease release, bool restartInBackground, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _archiveImportCancellation.Token);
        var prepared = false;
        _updatingApplication = true;
        _applicationUpdateProgress = "Downloading...";
        _applicationUpdatePercent = 0;
        _applicationUpdateError = "";
        RefreshApplicationUpdate();
        RefreshApplyActions();
        RefreshCompatibilityActions();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (!_updatingApplication || _applicationUpdatePercent < 0)
                {
                    return;
                }

                _applicationUpdatePercent = percent == 100 ? -1 : percent;
                _applicationUpdateProgress = percent == 100 ? "Preparing update..." : $"Downloading {percent}%";
                RefreshApplicationUpdate();
            });
            var update = await _applicationUpdates.PrepareAsync(release, progress, cancellation.Token, restartInBackground);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_pendingModArchives.Count > 0)
            {
                var automatic = restartInBackground && !HasAcceptedModInstallation;
                _applicationUpdates.PendingMods.Save(GameFolder, _pendingModArchives, automatic: automatic,
                    selectionFingerprint: automatic ? PendingSelectionFingerprint() : null);
            }
            prepared = true;
            _applicationUpdatePercent = -1;
            _applicationUpdateProgress = "Restarting...";
            return update;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        catch (Exception error) when (IsApplicationUpdateError(error))
        {
            _applicationUpdateError = "Could not update SnowPatcher. " + error.Message;
            return null;
        }
        finally
        {
            _updatingApplication = prepared;
            RefreshApplicationUpdate();
            RefreshApplyActions();
            RefreshCompatibilityActions();
        }
    }

    public void CancelApplicationUpdate(string? error = null)
    {
        _updatingApplication = false;
        _applicationUpdateError = error ?? "";
        RefreshApplicationUpdate();
        RefreshApplyActions();
        RefreshCompatibilityActions();
    }

    public void PostponeApplicationUpdate(PreparedApplicationUpdate update)
    {
        try
        { _applicationUpdates.DiscardPrepared(update); }
        finally { CancelApplicationUpdate(); }
        SetStatus("Automatic application update postponed.");
    }

    private void RefreshApplicationUpdate()
    {
        RaisePropertyChanged(nameof(ApplicationUpdateText));
        RaisePropertyChanged(nameof(ApplicationUpdateDetail));
        RaisePropertyChanged(nameof(HasApplicationUpdate));
        RaisePropertyChanged(nameof(CanUpdateApplication));
        RaisePropertyChanged(nameof(IsCheckingApplicationUpdate));
        RaisePropertyChanged(nameof(IsUpdatingApplication));
        RaisePropertyChanged(nameof(ApplicationUpdatePercent));
        RaisePropertyChanged(nameof(ApplicationUpdateError));
        RaisePropertyChanged(nameof(AvailableApplicationVersion));
        RaisePropertyChanged(nameof(HasSuitableApplicationUpdate));
        RaisePropertyChanged(nameof(CanAddMods));
        RaisePropertyChanged(nameof(CanEditMods));
        RefreshBackupActions();
    }

    private static bool IsApplicationUpdateError(Exception error) => IsLibraryError(error) ||
        error is HttpRequestException or JsonException or KeyNotFoundException or FormatException or OperationCanceledException;
}
