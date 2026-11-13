using SnowPatcher.Core.Mods;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task CheckUpdatesAsync()
    {
        if (_checkingUpdates || !_libraryReady || _isApplying || IsClosing)
        {
            return;
        }

        _checkingUpdates = true;
        try
        {
            foreach (var mod in Mods.ToArray())
            {
                if (_archiveImportCancellation.IsCancellationRequested || _isApplying)
                {
                    return;
                }

                if (mod.Source is null || mod.IsBusy)
                {
                    continue;
                }

                var checkedAt = mod.LastCheckedAt;
                if (checkedAt is not null && checkedAt <= DateTimeOffset.UtcNow &&
                    DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromHours(6))
                {
                    continue;
                }

                await CheckModAsync(mod);
                if (_modio.IsRateLimited)
                {
                    break;
                }
            }
        }
        finally { _checkingUpdates = false; }
    }

    private async Task CheckModAsync(ModItemViewModel mod)
    {
        if (mod.Source is not { } source)
        {
            return;
        }

        mod.SetBusy("Checking...");
        try
        {
            mod.SetChecked(await _modio.GetLatestAsync(source.ModId, _archiveImportCancellation.Token));
            SaveLibrary();
        }
        catch (OperationCanceledException) when (_archiveImportCancellation.IsCancellationRequested) { }
        catch (Exception error) when (IsLibraryError(error) || error is System.Text.Json.JsonException or KeyNotFoundException)
        {
            mod.SetCheckFailure(error.Message);
        }
        finally { mod.SetBusy(""); }
    }

    public async Task RunUpdateActionAsync(ModItemViewModel mod)
    {
        if (_isApplying || IsClosing)
        {
            return;
        }

        if (!mod.HasUpdateAction || mod.Source is not { } source)
        {
            return;
        }

        if (mod.NeedsRetry)
        {
            await CheckModAsync(mod);
            return;
        }
        var token = _archiveImportCancellation.Token;
        mod.SetBusy("Downloading...");
        try
        {
            await _archiveImportLock.WaitAsync(token);
            try
            {
                var release = await _modio.GetLatestAsync(source.ModId, token);
                mod.SetChecked(release);
                if (!mod.HasUpdate && !mod.IsUnavailable)
                {
                    SaveLibrary();
                    return;
                }
                var download = await _library.DownloadAsync(_modio, release, new Progress<int>(mod.SetProgress), token);
                token.ThrowIfCancellationRequested();
                await ReplaceArchiveAsync(mod, download.Archive, download.Source, token);
            }
            finally { _archiveImportLock.Release(); RefreshCompatibilityActions(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsLibraryError(error) || error is System.Text.Json.JsonException or KeyNotFoundException)
        {
            mod.SetCheckFailure(error.Message);
        }
        finally { mod.SetBusy(""); }
    }

    public async Task<IReadOnlyList<ModArchive>> GetPreviousVersionsAsync(ModItemViewModel mod)
    {
        if (_isApplying || IsClosing || !Mods.Contains(mod) || !mod.CanChooseVersion)
        {
            return [];
        }

        var token = _archiveImportCancellation.Token;
        await _archiveImportLock.WaitAsync(token);
        try
        {
            if (!Mods.Contains(mod))
            {
                return [];
            }

            mod.SetBusy("Loading versions...");
            return await _library.ReadPreviousAsync(mod.ToRecord(), token);
        }
        finally { mod.SetBusy(""); _archiveImportLock.Release(); RefreshCompatibilityActions(); }
    }

    public async Task UsePreviousVersionAsync(ModItemViewModel mod, ModArchive previous)
    {
        if (_isApplying || IsClosing || !Mods.Contains(mod) || mod.IsBusy || mod.ToRecord().PackageId == BuiltinMods.IndicatorId)
        {
            return;
        }

        var token = _archiveImportCancellation.Token;
        try
        {
            await _archiveImportLock.WaitAsync(token);
            try
            {
                if (!Mods.Contains(mod))
                {
                    return;
                }

                mod.SetBusy("Loading version...");
                var archive = await _library.ImportAsync(previous.SourcePath, previous.Sha256, token);
                await ReplaceArchiveAsync(mod, archive, null, token);
                SetStatus("Previous version selected. Click Apply to install it.");
            }
            finally { mod.SetBusy(""); _archiveImportLock.Release(); RefreshCompatibilityActions(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsLibraryError(error)) { SetStatus(error.Message); }
    }
}
