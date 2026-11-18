using System.Collections.Immutable;
using SnowPatcher.Core.Backups;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Installations;
using SnowPatcher.Core.Mods;

namespace SnowPatcher.Core.Patching;

public sealed record PatchCheckResult(CompatibilityReport Report, bool CanApply, bool HasPendingChanges, string? BlockReason, bool NeedsRecovery = false,
    bool GameNeedsReapply = false, bool MatchesAppliedSelection = false);
public sealed record PatchApplyResult(bool Changed, int FileCount, string BackupDirectory, bool Recovered = false, bool HasWarnings = false);
public sealed partial class PatchInstaller
{
    private readonly string _root;
    private readonly BackupStore _backups;
    public PatchInstaller(string? storageRoot = null)
    {
        _root = storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowPatcher", "Patches");
        _backups = storageRoot is null ? new BackupStore() : new BackupStore(Path.Combine(storageRoot, "Backups"));
    }

    public bool HasManagedState(GameInstallation installation)
    {
        var store = new PatchStorage(_root, installation);
        return store.ReadState() is not null || store.ReadJournal() is not null;
    }

    public GameInstallation? FindRecoverableInstallation()
    {
        if (!Directory.Exists(_root))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateDirectories(_root))
        {
            PatchStorage.RejectLinks(path);
            var statePath = Path.Combine(path, "state.json");
            var journalPath = Path.Combine(path, "pending.json");
            var metadata = File.Exists(journalPath) ? journalPath : statePath;
            if (!File.Exists(metadata))
            {
                continue;
            }

            try
            {
                using var input = File.OpenRead(metadata);
                using var document = System.Text.Json.JsonDocument.Parse(input);
                var installation = new GameInstallation(document.RootElement.GetProperty("InstallationPath").GetString()!);
                _ = GameInstallState.Read(installation);
                var store = new PatchStorage(_root, installation);
                if (Path.GetFullPath(path) == Path.GetFullPath(store.DirectoryPath) && HasManagedState(installation))
                {
                    return installation;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException or KeyNotFoundException)
            {
            }
        }

        return null;
    }

    public async Task<PatchCheckResult> CheckAsync(GameInstallation installation, IReadOnlyList<CompatibilityInput> inputs, CancellationToken token = default)
    {
        var selected = inputs.ToArray();
        return await Task.Run(async () =>
        {
            using var operation = _backups.AcquireLock(installation);
            return (await PrepareAsync(installation, selected, token).ConfigureAwait(false)).Check;
        }, token).ConfigureAwait(false);
    }

    public async Task<PatchApplyResult> ApplyAsync(GameInstallation installation, IReadOnlyList<CompatibilityInput> inputs, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var selected = inputs.ToArray();
        return await Task.Run(async () =>
        {
            GameSessionGuard.EnsureCanApply(installation);
            using var operation = _backups.AcquireLock(installation);
            var store = new PatchStorage(_root, installation);
            var recovering = store.ReadJournal() is not null;
            Recover(installation, store);
            if (recovering)
            {
                return new PatchApplyResult(true, 0, store.DirectoryPath, Recovered: true);
            }

            progress?.Report("Preparing mods...");
            var prepared = await PrepareAsync(installation, selected, token).ConfigureAwait(false);
            if (!prepared.Check.CanApply)
            {
                if (!prepared.Check.HasPendingChanges && prepared.Check.BlockReason is null && !prepared.Check.Report.HasBlockingIssues)
                {
                    return new PatchApplyResult(false, 0, store.DirectoryPath, HasWarnings: prepared.Check.Report.HasWarnings);
                }

                throw new InvalidOperationException(prepared.Check.BlockReason ?? "Resolve the compatibility issues before applying mods.");
            }

            var installationState = prepared.Installation!;
            progress?.Report("Saving backups...");
            if (File.Exists(installation.InitialPakPath))
            {
                await _backups.CreateSnapshotAsync(installation, installationState, "Before apply", token).ConfigureAwait(false);
            }

            foreach (var bytes in prepared.Baseline.Values)
            {
                token.ThrowIfCancellationRequested();
                store.SaveBlob(bytes);
            }

            var changes = new List<JournalFile>();
            foreach (var (relative, after) in prepared.Outputs)
            {
                var before = PatchStorage.ReadFile(PatchStorage.GamePath(installation, relative));
                if (PatchStorage.Hash(before) == PatchStorage.Hash(after))
                {
                    continue;
                }

                store.SaveBlob(before);
                store.SaveBlob(after);
                changes.Add(new JournalFile(relative, PatchStorage.Hash(before), PatchStorage.Hash(after)));
            }

            var id = Guid.NewGuid().ToString("N");
            var next = new ManagedState(1, id, store.InstallationPath, installationState.BuildId, installationState.Fingerprint, prepared.Baseline.Select(p => new ManagedFile(p.Key, PatchStorage.Hash(p.Value), PatchStorage.Hash(prepared.Outputs[p.Key]))).ToArray(), prepared.Mods);
            if (prepared.Previous is not null)
            {
                store.SaveHistory(id, prepared.Previous);
            }

            var journal = new PatchJournal(1, id, store.InstallationPath, installationState.BuildId, installationState.Fingerprint, changes.ToArray());
            // Starting files and preimages are checksum-verified and durable before touching the game folder.
            foreach (var file in next.Files)
            {
                _ = store.Blob(file.BaselineHash);
            }

            foreach (var file in changes)
            {
                _ = store.Blob(file.BeforeHash);
                _ = store.Blob(file.AfterHash);
            }

            ValidateInputs(installation, prepared, selected, token);
            store.SaveJournal(journal);
            var committed = false;
            try
            {
                progress?.Report("Applying mods...");
                foreach (var file in changes.Where(f => f.AfterHash != "absent"))
                {
                    StageFile(installation, store, journal.Id, file.Path, file.AfterHash);
                }

                ValidateInputs(installation, prepared, selected, token);
                foreach (var file in changes)
                {
                    token.ThrowIfCancellationRequested();
                    GameSessionGuard.EnsureCanApply(installation);
                    installationState.EnsureUnchanged(installation);
                    InstallFile(installation, store, journal.Id, file.Path, file.BeforeHash, file.AfterHash, installationState);
                }

                ValidateInputs(installation, prepared, selected, CancellationToken.None, installedOutputs: true);
                foreach (var file in next.Files)
                {
                    RequireHash(PatchStorage.GamePath(installation, file.Path), file.OutputHash);
                }

                store.SaveState(next);
                committed = true;
            }
            catch (Exception error)
            {
                try
                {
                    Recover(installation, store);
                }
                catch (Exception recoveryError)
                {
                    throw new IOException($"Apply stopped. Recovery needs attention; keep the backups at {store.DirectoryPath}.", new AggregateException(error, recoveryError));
                }

                throw new IOException("Apply failed. The previous game files were restored.", error);
            }
            finally
            {
                if (committed)
                {
                    Cleanup(installation, store, journal);
                }
            }

            return new PatchApplyResult(changes.Count > 0, changes.Count, store.DirectoryPath, HasWarnings: prepared.Check.Report.HasWarnings);
        }, token).ConfigureAwait(false);
    }
}
