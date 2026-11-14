using System.ComponentModel;
using SnowPatcher.Core;
using SnowPatcher.Core.Mods;

namespace SnowPatcher.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Task InitializeLibraryAsync() => IsClosing ? Task.CompletedTask : _initializationTask ??= LoadLibraryAsync();

    private async Task LoadLibraryAsync()
    {
        var token = _archiveImportCancellation.Token;
        try
        {
            var records = (await Task.Run(_library.Load, token)).ToList();
            if (_includeBundledMods)
            {
                var bundled = await Task.Run(() => BuiltinMods.EnsureIndicator(), token);
                var index = records.FindIndex(m => m.PackageId == BuiltinMods.IndicatorId);
                if (index < 0)
                {
                    records.Add(new LibraryMod(Guid.NewGuid(), bundled.SourcePath, bundled.Sha256, true, null, null, null, [], BuiltinMods.IndicatorId));
                }
                else if (index >= 0 && records[index].Source is null && BuiltinMods.OwnsArchive(records[index].SourcePath) && records[index].Sha256 != bundled.Sha256)
                {
                    var previous = records[index];
                    records[index] = previous with
                    {
                        SourcePath = bundled.SourcePath,
                        Sha256 = bundled.Sha256,
                        PreviousDownloads = [.. previous.PreviousDownloads, previous.SourcePath]
                    };
                }
            }
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                ModItemViewModel mod;
                try
                {
                    var archive = record.PackageId == BuiltinMods.IndicatorId || _library.OwnsArchive(record.SourcePath)
                        ? await Task.Run(() => ModArchiveReader.Read(record.SourcePath, token), token)
                        : await _library.ImportAsync(record.SourcePath, record.Sha256, token);
                    if (archive.Sha256 != record.Sha256)
                    {
                        throw new InvalidDataException("The saved ZIP changed.");
                    }

                    mod = new ModItemViewModel(archive, record with
                    {
                        SourcePath = archive.SourcePath
                    });
                }
                catch (Exception error) when (IsLibraryError(error))
                {
                    mod = new ModItemViewModel(new ModArchive(record.SourcePath, 0), record, unavailable: true,
                        unavailableReason: error.Message, requiredSnowPatcherVersion: (error as SnowPatcherUpdateRequiredException)?.RequiredVersion);
                    if (error is SnowPatcherUpdateRequiredException required)
                    {
                        var deferred = required.Archive;
                        if (deferred is null)
                        {
                            try
                            { deferred = await _library.DeferAsync(record.SourcePath, required.ModName ?? record.DisplayName ?? mod.Name, required.RequiredVersion, record.Source, record.Sha256, token); }
                            catch (Exception retentionError) when (IsLibraryError(retentionError)) { SetStatus(retentionError.Message); }
                        }

                        NoticeApplicationRequirement(error, deferred);
                    }
                }
                AddMod(mod);
            }
            token.ThrowIfCancellationRequested();
            _library.Save(Mods.Select(m => m.ToRecord()), initializeBundledMods: _includeBundledMods);
            _libraryReady = true;
            RaisePropertyChanged(nameof(CanAddMods));
            RequestCompatibilityCheck();
            await CheckUpdatesAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsLibraryError(error)) { _libraryError = error.Message; SetStatus(_libraryError); }
    }

    private void AddMod(ModItemViewModel mod)
    {
        mod.PropertyChanged += OnModChanged;
        if (mod.ToRecord().PackageId == BuiltinMods.IndicatorId)
        {
            Mods.Insert(0, mod);
        }
        else
        {
            Mods.Add(mod);
        }

        RefreshModCount();
    }

    private async Task<bool> AcceptArchiveAsync(ModArchive archive, ModioSource? source, CancellationToken token)
    {
        var packageId = archive.Package?.Manifest.Id;
        if (packageId == BuiltinMods.IndicatorId)
        {
            throw new InvalidDataException("The SnowPatcher indicator is built in and cannot be replaced by an imported ZIP.");
        }

        var existing = packageId is null ? null : Mods.FirstOrDefault(m => m.ToRecord().PackageId == packageId);
        existing ??= Mods.FirstOrDefault(m => m.ToRecord().Sha256 == archive.Sha256);
        if (existing is not null)
        {
            if (!existing.IsUnavailable && existing.SourcePath == archive.SourcePath && existing.Archive.Sha256 == archive.Sha256 && source is null &&
                (packageId is null || Mods.Count(m => m.ToRecord().PackageId == packageId) == 1))
            {
                return false;
            }

            await ReplaceArchiveAsync(existing, archive, source, token);
        }
        else
        {
            token.ThrowIfCancellationRequested();
            var record = new LibraryMod(Guid.NewGuid(), archive.SourcePath, archive.Sha256, true, source,
                source is null ? null : DateTimeOffset.UtcNow, source, [], packageId, archive.Package?.Manifest.ModioUrl,
                source?.Name ?? archive.DisplayName);
            _library.Save(Mods.Select(m => m.ToRecord()).Append(record));
            AddMod(new ModItemViewModel(archive, record));
        }
        return true;
    }

    private async Task ReplaceArchiveAsync(ModItemViewModel mod, ModArchive archive, ModioSource? source, CancellationToken token)
    {
        var current = mod.ToRecord();
        if (current.PackageId is not null && archive.Package?.Manifest.Id != current.PackageId)
        {
            throw new InvalidDataException("The update has a different package ID. The current mod was kept.");
        }

        var duplicates = Mods.Where(m => m != mod && (current.PackageId is not null
            ? m.ToRecord().PackageId == current.PackageId : m.ToRecord().Sha256 == archive.Sha256)).ToArray();
        var previous = new[] { mod }.Concat(duplicates).ToArray();
        foreach (var entry in previous)
        {
            entry.SetBusy("Updating...");
        }

        try
        {
            var history = previous.SelectMany(m => m.ToRecord().PreviousDownloads).ToList();
            foreach (var entry in previous)
            {
                if (entry.IsUnavailable || entry.ToRecord().Sha256 == archive.Sha256)
                {
                    continue;
                }

                try
                {
                    var retained = await _library.ImportAsync(entry.SourcePath, entry.ToRecord().Sha256, token);
                    history.Add(retained.SourcePath);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
                {
                    // A missing or altered old ZIP cannot be retained as a verified version.
                    // Its remaining files are left alone; a valid replacement can still repair the entry.
                }
            }
            token.ThrowIfCancellationRequested();
            var sameBytes = current.Sha256 == archive.Sha256;
            var record = current with
            {
                SourcePath = archive.SourcePath,
                Sha256 = archive.Sha256,
                PackageId = archive.Package?.Manifest.Id,
                Source = source ?? (sameBytes ? current.Source : null),
                Latest = source ?? (sameBytes ? current.Latest : null),
                LastCheckedAt = source is not null ? DateTimeOffset.UtcNow : sameBytes ? current.LastCheckedAt : null,
                PreviousDownloads = history.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray(),
                ModioUrl = archive.Package?.Manifest.ModioUrl ?? current.ModioUrl ?? mod.ModPage?.AbsoluteUri,
                DisplayName = archive.Package?.Manifest.Name ?? source?.Name ?? mod.Name
            };
            _library.Save(Mods.Where(m => !duplicates.Contains(m)).Select(m => m == mod ? record : m.ToRecord()));
            mod.UseArchive(archive, record);
            foreach (var duplicate in duplicates)
            {
                Mods.Remove(duplicate);
                duplicate.PropertyChanged -= OnModChanged;
            }
            if (duplicates.Length > 0)
            {
                RefreshModCount();
            }
        }
        finally
        {
            foreach (var entry in previous)
            {
                entry.SetBusy("");
            }
        }
    }

    private void OnModChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModItemViewModel.IsEnabled) && !_revertingToggle)
        {
            if (!CanEditMods || !SaveLibrary())
            {
                _revertingToggle = true;
                try
                {
                    ((ModItemViewModel)sender!).IsEnabled = !((ModItemViewModel)sender!).IsEnabled;
                }
                finally { _revertingToggle = false; }
                return;
            }
            RequestCompatibilityCheck();
        }
        else if (args.PropertyName == nameof(ModItemViewModel.Archive))
        {
            RequestCompatibilityCheck();
        }
        else if (args.PropertyName == nameof(ModItemViewModel.IsBusy))
        {
            RefreshCompatibilityActions();
        }
    }

    private bool SaveLibrary()
    {
        if (!_libraryReady || _archiveImportCancellation.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            _library.Save(Mods.Select(m => m.ToRecord()));
            return true;
        }
        catch (Exception error) when (IsLibraryError(error)) { SetStatus($"Could not save the mod list: {error.Message}"); return false; }
    }

    private void RefreshModCount()
    {
        RaisePropertyChanged(nameof(IsEmpty));
        RaisePropertyChanged(nameof(ModCountText));
        RequestCompatibilityCheck();
    }

    public void RemoveMod(ModItemViewModel mod)
    {
        if (!mod.CanRemove || !CanEditMods || !Mods.Contains(mod))
        {
            return;
        }

        try
        {
            _library.Save(Mods.Where(m => m != mod).Select(m => m.ToRecord()));
        }
        catch (Exception error) when (IsLibraryError(error)) { SetStatus($"Could not save the mod list: {error.Message}"); return; }
        Mods.Remove(mod);
        mod.PropertyChanged -= OnModChanged;
        RefreshModCount();
        SetStatus("");
    }
}
