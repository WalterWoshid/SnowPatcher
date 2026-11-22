using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Patching;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? _attemptedQueuedSelection;

    private void SaveSelection(IEnumerable<LibraryMod> mods)
    {
        var records = mods.ToArray();
        if (_pendingInstallationRunning)
        {
            _library.Save(records);
            return;
        }

        _library.SaveSelection(records, EnabledSelectionFingerprint(records, GameFolder));
        _attemptedQueuedSelection = null;
    }

    private static string EnabledSelectionFingerprint(IEnumerable<LibraryMod> mods, string folder) =>
        SelectionFingerprint(mods.Where(m => m.IsEnabled), folder);

    private bool HasQueuedSelection() => _library.QueuedSelection is { } queued &&
        queued == EnabledSelectionFingerprint(Mods.Select(m => m.ToRecord()), GameFolder);

    private void BindUnassignedSelection()
    {
        if (_library.QueuedSelection is not { } queued)
        {
            return;
        }
        var records = Mods.Select(m => m.ToRecord()).ToArray();
        // Imports can finish before the installation has been located.
        if (queued == EnabledSelectionFingerprint(records, ""))
        {
            _library.SaveSelection(records, EnabledSelectionFingerprint(records, GameFolder));
        }
    }

    public async Task ApplyQueuedSelectionAsync()
    {
        if (!IsReadyForMaintenance || _patchCheck is not { } check ||
            _applyTask is { IsCompleted: false } || _library.QueuedSelection == _attemptedQueuedSelection ||
            !HasQueuedSelection())
        {
            return;
        }

        if (!check.HasPendingChanges && check.BlockReason is null && !check.Report.HasBlockingIssues)
        {
            ClearQueuedSelection();
            return;
        }

        if (!CanApplySelection || check.NeedsRecovery || check.Report.HasBlockingIssues || check.BlockReason is not null)
        {
            return;
        }

        if (AutomaticActionsPaused || IsGameRunning)
        {
            SetCompatibilitySummary(SelectionSummary(check));
            return;
        }

        var request = _library.QueuedSelection;
        _attemptedQueuedSelection = request;
        await ApplyModsAsync();
        if (_library.QueuedSelection == request && IsGameRunning)
        {
            // A launch between the process check and Apply can defer the same request again.
            _attemptedQueuedSelection = null;
            SetStatus("");
            SetCompatibilitySummary(SelectionSummary(check));
        }
    }

    private void ClearQueuedSelection()
    {
        if (!HasQueuedSelection())
        {
            return;
        }

        try
        {
            _library.SaveSelection(Mods.Select(m => m.ToRecord()), null);
        }
        catch (Exception error) when (IsLibraryError(error))
        {
            _attemptedQueuedSelection = _library.QueuedSelection;
            SetStatus("Could not clear the queued mod changes. " + error.Message);
        }
    }

    private string SelectionSummary(PatchCheckResult check)
    {
        var report = check.Report;
        if (report.Mods.FirstOrDefault(m => m.HasBlockingIssues) is { } blocked)
        {
            return $"{blocked.Name} - {blocked.Issues.First(i => i.IsBlocking).Message} Click the check result for details.";
        }

        if (check.BlockReason is { } reason)
        {
            return reason;
        }

        if (check.HasPendingChanges)
        {
            if (!HasQueuedSelection())
            {
                return "Changes not applied.";
            }
            if (AutomaticActionsPaused)
            {
                return "Pending - automatic actions are paused.";
            }

            if (IsGameRunning)
            {
                return "Pending - applies when SnowRunner closes.";
            }

            return _library.QueuedSelection == _attemptedQueuedSelection ? "Changes not applied." : "Pending - waiting to apply.";
        }

        return report.HasWarnings ? "Mods applied. Some translations were skipped. Click a check result for details."
            : report.Mods.Length > 0 ? "Mods applied." : "";
    }
}
