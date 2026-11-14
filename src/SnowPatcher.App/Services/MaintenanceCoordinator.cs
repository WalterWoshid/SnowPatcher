using SnowPatcher.App.ViewModels;
using SnowPatcher.Core;
using SnowPatcher.Core.Settings;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.App.Services;

public sealed class MaintenanceCoordinator(MainWindowViewModel main)
{
    private string? _gameKey;
    private DateTimeOffset _lastUpdateCheck = DateTimeOffset.UtcNow;
    private bool _firstCheck = true;
    private bool _running;
    private string? _shownGameAttention;

    public async Task<MaintenanceResult> RunAsync(bool canRestartApplication, bool paused = false, bool checkUpdatesNow = false)
    {
        if (_running || !main.IsReadyForMaintenance)
        {
            return new MaintenanceResult([]);
        }

        _running = true;
        bool Paused() => paused || main.AutomaticActionsPaused;
        var notices = new List<UserNotice>();
        try
        {
            if (main.GameFolder.Length > 0)
            {
                GameChangeSnapshot snapshot;
                try
                { snapshot = await Task.Run(() => GameChangeProbe.Read(new GameInstallation(main.GameFolder))); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Steam may be replacing files. A later tick checks the committed installation.
                    return new MaintenanceResult([]);
                }

                if (_gameKey is null || _gameKey != snapshot.Key)
                {
                    if (_gameKey is not null)
                    {
                        await main.SelectGameFolderAsync(main.GameFolder);
                    }
                    await main.CheckCompatibilityAsync();
                }
                _gameKey = snapshot.Key;
            }

            if (!main.IsReadyForMaintenance)
            {
                return new MaintenanceResult([]);
            }

            var due = checkUpdatesNow || DateTimeOffset.UtcNow - _lastUpdateCheck >= TimeSpan.FromMinutes(30);
            var automaticUpdateDue = due || _firstCheck;
            _firstCheck = false;
            var preferences = main.CurrentPreferences;
            if (due)
            {
                _lastUpdateCheck = DateTimeOffset.UtcNow;
                await Task.WhenAll(preferences.CheckApplicationUpdates ? main.CheckApplicationUpdatesAsync() : Task.CompletedTask,
                    preferences.CheckModUpdates ? main.CheckUpdatesAsync(force: checkUpdatesNow) : Task.CompletedTask);
            }
            preferences = main.CurrentPreferences;

            if (!Paused() && main.CanResumeModInstallation && !main.IsGameRunning && !main.NeedsManualRecovery)
            {
                if (await main.ResumeModInstallationAsync())
                {
                    notices.Add(new UserNotice("install:" + main.PendingInstallationText, "Mods installed", main.PendingInstallationText, false));
                }
                else
                {
                    notices.Add(new UserNotice("install-failed:" + main.PendingInstallationText, "Mod installation needs attention", main.PendingInstallationText, true));
                }
            }

            preferences = main.CurrentPreferences;
            if (!Paused() && automaticUpdateDue && preferences.AutomaticallyUpdateMods && main.IsReadyForMaintenance)
            {
                var update = await main.UpdateModsAutomaticallyAsync();
                if (update.Updated > 0)
                {
                    notices.Add(new UserNotice("mods-updated:" + SelectionKey(), "Mods updated", $"Updated and applied {update.Updated} {(update.Updated == 1 ? "mod" : "mods")}.", false));
                }
                else if (update.Error is { } error)
                {
                    notices.Add(new UserNotice("mod-update-error:" + error, "A mod update needs attention", error, true));
                }
            }

            var showApplication = false;
            preferences = main.CurrentPreferences;
            if (main.GameNeedsReapply)
            {
                var key = "reapply:" + main.Inspection?.BuildId + ":" + SelectionKey();
                if (!Paused() && preferences.GameUpdates == GameUpdateBehavior.ReapplyMods && main.MatchesAppliedSelection &&
                    main.IsReadyForMaintenance && !main.IsGameRunning && !main.NeedsManualRecovery && main.AutomaticBlockReason is null)
                {
                    if (await main.ReapplyModsAutomaticallyAsync())
                    {
                        var count = main.Mods.Count(m => m.IsEnabled);
                        notices.Add(new UserNotice(key + ":done", "Mods reapplied", $"Reapplied {count} {(count == 1 ? "mod" : "mods")} after the SnowRunner update.", false));
                    }
                    else
                    {
                        notices.Add(new UserNotice(key + ":failed", "Mods need attention", main.StatusText, true));
                    }
                }
                else if (preferences.GameUpdates != GameUpdateBehavior.ReapplyMods || main.AutomaticBlockReason is not null ||
                    !main.MatchesAppliedSelection || main.NeedsManualRecovery)
                {
                    var detail = main.NeedsManualRecovery ? "Open SnowPatcher to recover an interrupted change." : main.AutomaticBlockReason ??
                        (!main.MatchesAppliedSelection ? "Review your changed mod selection in SnowPatcher before applying it." : "SnowRunner changed. Open SnowPatcher to reapply your mods.");
                    notices.Add(new UserNotice(key + ":" + detail, "Mods need reapplying", detail, true));
                    showApplication = preferences.GameUpdates == GameUpdateBehavior.ShowApplication && _shownGameAttention != key;
                    if (showApplication)
                    {
                        _shownGameAttention = key;
                    }
                }
            }
            else
            {
                _shownGameAttention = null;
            }

            preferences = main.CurrentPreferences;
            if (main.HasApplicationUpdate && preferences.CheckApplicationUpdates)
            {
                var canResumeMods = !main.HasModInstallRequest || preferences.AutomaticallyUpdateMods || main.HasAcceptedModInstallation;
                if (!Paused() && preferences.AutomaticallyUpdateApplication && canRestartApplication && main.CanUpdateApplication && !main.IsGameRunning && canResumeMods)
                {
                    var update = await main.PrepareApplicationUpdateAsync(restartInBackground: true);
                    if (update is not null)
                    {
                        return new MaintenanceResult(notices, showApplication, update);
                    }
                    if (main.ApplicationUpdateError.Length > 0)
                    {
                        notices.Add(new UserNotice("app-update-failed:" + main.ApplicationUpdateError, "SnowPatcher could not update", main.ApplicationUpdateError, true));
                    }
                }
                else if (!preferences.AutomaticallyUpdateApplication)
                {
                    notices.Add(new UserNotice("app-update:" + main.AvailableApplicationVersion, "SnowPatcher update available", "SnowPatcher " + main.AvailableApplicationVersion + " is available.", true));
                }
                else if (main.NeedsNewerApplication)
                {
                    notices.Add(new UserNotice("requires-app:" + main.RequiredSnowPatcherVersion, "A mod needs a newer SnowPatcher", "Open SnowPatcher to finish installing the mod.", true));
                }
            }

            if (!preferences.AutomaticallyUpdateMods && preferences.CheckModUpdates && main.Mods.Any(m => m.HasUpdate))
            {
                var names = string.Join(", ", main.Mods.Where(m => m.HasUpdate).Select(m => m.Name));
                notices.Add(new UserNotice("mod-updates:" + names, "Mod updates available", "Updates are available for " + names + ".", true));
            }

            return new MaintenanceResult(notices, showApplication);
        }
        catch (OperationCanceledException) when (main.IsClosing) { return new MaintenanceResult([]); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            main.SetStatus(error.Message);
            return new MaintenanceResult([new UserNotice("maintenance:" + error.Message, "SnowPatcher needs attention", error.Message, true)]);
        }
        finally { _running = false; }
    }

    private string SelectionKey() => string.Join('|', main.Mods.Where(m => m.IsEnabled).Select(m => m.Archive.Sha256));
}
