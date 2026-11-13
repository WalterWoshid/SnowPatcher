using System.Collections.Immutable;
using SnowPatcher.Core.Backups;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Patching;

public sealed partial class PatchInstaller
{
    private static void ValidateInputs(GameInstallation installation, Prepared prepared, CompatibilityInput[] inputs, CancellationToken token, bool installedOutputs = false)
    {
        token.ThrowIfCancellationRequested();
        GameSessionGuard.EnsureCanApply(installation);
        prepared.Steam!.EnsureUnchanged(installation);
        var outputs = installedOutputs ? prepared.Outputs.ToDictionary(p => PatchStorage.GamePath(installation, p.Key), p => PatchStorage.Hash(p.Value), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) : null;
        foreach (var file in prepared.Check.Report.GameFiles)
        {
            token.ThrowIfCancellationRequested();
            RequireHash(file.Path, outputs?.GetValueOrDefault(file.Path) ?? file.Sha256);
        }

        foreach (var input in inputs)
        {
            if (ModArchiveReader.Read(input.Archive.SourcePath, token).Sha256 != input.Archive.Sha256)
            {
                throw new IOException("A mod ZIP changed while preparing Apply. Add it again.");
            }
        }
    }

    private static void Recover(GameInstallation installation, PatchStorage store)
    {
        var journal = store.ReadJournal();
        if (journal is null)
        {
            return;
        }

        GameSessionGuard.EnsureCanApply(installation);
        var steam = SteamInstallState.Read(installation);
        if (steam.BuildId != journal.BuildId || steam.DepotFingerprint != journal.DepotFingerprint)
        {
            throw new IOException("Steam changed the game build during an interrupted apply. Recovery copies were kept; no files were overwritten.");
        }

        var committed = store.ReadState()?.TransactionId == journal.Id;
        // Check every target before recovering any of them. Preserve files changed by another tool.
        foreach (var file in journal.Files)
        {
            var hash = PatchStorage.Hash(PatchStorage.ReadFile(PatchStorage.GamePath(installation, file.Path)));
            if (hash != file.AfterHash && (committed || hash != file.BeforeHash))
            {
                throw new IOException($"{file.Path} changed outside this transaction. Its recovery copies were kept.");
            }

            _ = store.Blob(file.BeforeHash);
            _ = store.Blob(file.AfterHash);
        }

        if (!committed)
        {
            foreach (var file in journal.Files.Reverse())
            {
                steam.EnsureUnchanged(installation);
                GameSessionGuard.EnsureCanApply(installation);
                var path = PatchStorage.GamePath(installation, file.Path);
                if (PatchStorage.Hash(PatchStorage.ReadFile(path)) == file.BeforeHash)
                {
                    continue;
                }

                InstallFile(installation, store, journal.Id + "-recovery", file.Path, file.AfterHash, file.BeforeHash, steam);
            }
        }

        Cleanup(installation, store, journal);
    }

    private static void InstallFile(GameInstallation installation, PatchStorage store, string id, string relative, string before, string after, SteamInstallState steam)
    {
        var target = PatchStorage.GamePath(installation, relative);
        if (!Directory.Exists(Path.GetDirectoryName(target)))
        {
            throw new IOException("The target folder is missing. No folders are created inside the game.");
        }

        var staged = target + ".snowpatcher-" + id + ".stage";
        var rollback = target + ".snowpatcher-" + id + ".rollback";
        RequireHash(target, before);
        if (after == "absent")
        {
            GameSessionGuard.EnsureCanApply(installation);
            steam.EnsureUnchanged(installation);
            RequireHash(target, before);
            File.Move(target, rollback);
        }
        else
        {
            StageFile(installation, store, id, relative, after);
            RequireHash(staged, after);
            RequireHash(target, before);
            if (!OperatingSystem.IsWindows() && before != "absent")
            {
                File.SetUnixFileMode(staged, File.GetUnixFileMode(target));
            }

            GameSessionGuard.EnsureCanApply(installation);
            steam.EnsureUnchanged(installation);
            RequireHash(target, before);
            if (before == "absent")
            {
                File.Move(staged, target);
            }
            else
            {
                File.Replace(staged, target, rollback);
            }
        }

        RequireHash(target, after);
    }

    private static void StageFile(GameInstallation installation, PatchStorage store, string id, string relative, string after)
    {
        var staged = PatchStorage.GamePath(installation, relative) + ".snowpatcher-" + id + ".stage";
        if (File.Exists(staged))
        {
            RequireHash(staged, after);
            return;
        }

        PatchStorage.SaveNew(staged, store.Blob(after)!);
        RequireHash(staged, after);
    }

    private static void Cleanup(GameInstallation installation, PatchStorage store, PatchJournal journal)
    {
        foreach (var file in journal.Files)
        {
            foreach (var suffix in new[]
            {
                "",
                "-recovery"
            }

            )
            {
                foreach (var extension in new[]
                {
                    ".stage",
                    ".rollback"
                }

                )
                {
                    var path = PatchStorage.GamePath(installation, file.Path) + ".snowpatcher-" + journal.Id + suffix + extension;
                    try
                    {
                        if (!File.Exists(path))
                        {
                            continue;
                        }

                        var hash = PatchStorage.Hash(PatchStorage.ReadFile(path));
                        if (hash == file.BeforeHash || hash == file.AfterHash)
                        {
                            File.Delete(path);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        return;
                    }
                }
            }
        }

        try
        {
            File.Delete(store.JournalPath);
        }
        catch (IOException)
        {
        }
    }

    private static void RequireHash(string path, string expected)
    {
        if (PatchStorage.Hash(PatchStorage.ReadFile(path)) != expected)
        {
            throw new IOException($"{Path.GetFileName(path)} changed during Apply. Existing files were kept.");
        }
    }
}
