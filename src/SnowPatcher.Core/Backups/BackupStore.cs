using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Backups;

public sealed class BackupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;

    public BackupStore(string? rootDirectory = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(data))
        {
            throw new IOException("Your user data folder could not be located.");
        }

        _root = rootDirectory ?? Path.Combine(data, "SnowPatcher", "Backups");
    }

    public async Task<ArchiveBackup> EnsureBackupAsync(GameInstallation installation, string expectedBuild, CancellationToken cancellationToken = default)
    {
        using var operation = AcquireLock(installation);
        var state = SteamInstallState.Read(installation);
        if (state.BuildId != expectedBuild)
        {
            throw new IOException("The installed build changed. Select the game folder again.");
        }

        return await CreateSnapshotAsync(installation, state, "Initial backup", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArchiveBackup>> ListAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        var state = SteamInstallState.Read(installation);
        var directory = GetInstallationDirectory(installation);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<ArchiveBackup>();
        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _))
            {
                continue;
            }

            try
            {
                var backup = await ReadRecordAsync(path, cancellationToken).ConfigureAwait(false);
                if (backup is not null && Matches(backup, installation, state) && File.Exists(Path.Combine(path, "initial.pak")))
                {
                    result.Add(backup);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidDataException) { }
        }

        return result.OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    public async Task<BackupRestoreResult> RestoreAsync(GameInstallation installation, ArchiveBackup selected, CancellationToken cancellationToken = default)
    {
        GameSessionGuard.EnsureCanRestore(installation);
        using var operation = AcquireLock(installation);
        var state = SteamInstallState.Read(installation);
        var directory = GetRecordDirectory(installation, selected.Id);
        var backup = await ReadRecordAsync(directory, cancellationToken).ConfigureAwait(false);
        if (backup is null || !SameContent(backup, selected) || !Matches(backup, installation, state))
        {
            throw new InvalidDataException("This backup does not match the current game installation and build.");
        }

        var backupPath = Path.Combine(directory, "initial.pak");
        RejectLink(backupPath);
        if (!await HasExpectedContentAsync(backupPath, backup, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The backup failed its integrity check. The game was not changed.");
        }

        var before = await CreateSnapshotAsync(installation, state, "Before restore", cancellationToken).ConfigureAwait(false);
        if (before.Sha256 == backup.Sha256 && before.Length == backup.Length)
        {
            return new BackupRestoreResult(false);
        }

        var target = installation.InitialPakPath;
        var staged = target + ".snowpatcher-" + Guid.NewGuid().ToString("N") + ".tmp";
        var recovery = target + ".snowpatcher-" + Guid.NewGuid().ToString("N") + ".rollback";
        var replaced = false;
        try
        {
            var copied = await CopyArchiveAsync(backupPath, staged, cancellationToken).ConfigureAwait(false);
            if (copied.Hash != backup.Sha256 || copied.Length != backup.Length)
            {
                throw new InvalidDataException("The backup changed while being prepared. The game was not changed.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staged, File.GetUnixFileMode(target));
            }

            GameSessionGuard.EnsureCanRestore(installation);
            state.EnsureUnchanged(installation);
            RejectLink(target);
            if (!await HasExpectedContentAsync(target, before, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("initial.pak changed during restoration. Try again after other tools have finished.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Replacement and its recovery copy are on the same volume. Never overwrite in place.
            File.Replace(staged, target, recovery);
            replaced = true;
            if (!await HasExpectedContentAsync(target, backup, CancellationToken.None).ConfigureAwait(false))
            {
                throw new IOException("The restored file failed its integrity check.");
            }

            state.EnsureUnchanged(installation);
            File.Delete(recovery);
            return new BackupRestoreResult(true);
        }
        catch (Exception error) when (replaced)
        {
            try
            {
                File.Replace(recovery, target, null);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Restore failed. Your previous archive is preserved at {recovery} and in SnowPatcher's backups.",
                    new AggregateException(error, rollbackError));
            }
            throw new IOException("Restore failed. Your previous archive was put back.", error);
        }
        finally
        {
            TryDeleteFile(staged);
        }
    }

    public async Task<ArchiveBackup> RenameAsync(GameInstallation installation, ArchiveBackup selected, string? label,
        CancellationToken cancellationToken = default)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is not null && label.Any(char.IsControl))
        {
            throw new ArgumentException("Use a single line without control characters for the backup name.");
        }

        var directory = GetRecordDirectory(installation, selected.Id);
        Patching.PatchStorage.RejectLinks(directory);
        if (!Directory.Exists(directory))
        {
            throw new InvalidDataException("This backup is no longer available.");
        }
        // Labels are independent of game writes. They can be edited while the
        // game is running or a compatibility check holds the operation lock.
        var labelLockPath = Path.Combine(GetInstallationDirectory(installation), "labels.lock");
        Patching.PatchStorage.RejectLinks(labelLockPath);
        using var labelLock = new FileStream(labelLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = await ReadRecordAsync(directory, cancellationToken).ConfigureAwait(false);
        if (current is null || current.InstallationPath != NormalizeRoot(installation) || !SameContent(current, selected))
        {
            throw new InvalidDataException("This backup changed. Reopen the backup list and try again.");
        }

        var updated = current with
        {
            Label = label
        };
        if (updated == current)
        {
            return current;
        }

        var metadataPath = Path.Combine(directory, "backup.json");
        Patching.PatchStorage.RejectLinks(metadataPath);
        var temporary = Path.Combine(directory, "backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, updated, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(temporary, metadataPath, null);
            return updated;
        }
        finally { TryDeleteFile(temporary); }
    }

    internal async Task<ArchiveBackup> CreateSnapshotAsync(GameInstallation installation, SteamInstallState state, string reason,
        CancellationToken cancellationToken)
    {
        RejectLink(installation.InitialPakPath);
        var directory = GetInstallationDirectory(installation);
        var id = Guid.NewGuid().ToString("N");
        var pending = Path.Combine(directory, ".pending-" + id);
        var destination = GetRecordDirectory(installation, id);
        Directory.CreateDirectory(pending);
        try
        {
            var contentPath = Path.Combine(pending, "initial.pak");
            var copied = await CopyArchiveAsync(installation.InitialPakPath, contentPath, cancellationToken).ConfigureAwait(false);
            state.EnsureUnchanged(installation);
            var record = new ArchiveBackup(1, id, NormalizeRoot(installation), state.BuildId, state.DepotFingerprint,
                DateTimeOffset.UtcNow, copied.Hash, copied.Length, reason);
            if (!await HasExpectedContentAsync(contentPath, record, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The backup failed its integrity check.");
            }

            foreach (var existing in await ListAsync(installation, cancellationToken).ConfigureAwait(false))
            {
                if (existing.Sha256 != record.Sha256 || existing.Length != record.Length)
                {
                    continue;
                }

                try
                {
                    if (await HasExpectedContentAsync(Path.Combine(GetRecordDirectory(installation, existing.Id), "initial.pak"), existing, cancellationToken).ConfigureAwait(false))
                    {
                        return existing;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            var metadata = Path.Combine(pending, "backup.json");
            await using (var output = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, record, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            Directory.Move(pending, destination);
            return record;
        }
        finally
        {
            // Only our two known temporary files are removed. Unexpected files are preserved.
            TryDeleteFile(Path.Combine(pending, "initial.pak"));
            TryDeleteFile(Path.Combine(pending, "backup.json"));
            try
            {
                if (Directory.Exists(pending))
                {
                    Directory.Delete(pending);
                }
            }
            catch (IOException) { }
        }
    }

    private static async Task<(string Hash, long Length)> CopyArchiveAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            copied += count;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        info.Refresh();
        if (!info.Exists || copied != length || info.Length != length || info.LastWriteTimeUtc != modified)
        {
            throw new IOException("The archive changed while the backup was being copied.");
        }

        return (Convert.ToHexString(hash.GetHashAndReset()), copied);
    }

    private static async Task<bool> HasExpectedContentAsync(string path, ArchiveBackup backup, CancellationToken cancellationToken)
    {
        RejectLink(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return stream.Length == backup.Length &&
            Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == backup.Sha256;
    }

    private static async Task<ArchiveBackup?> ReadRecordAsync(string directory, CancellationToken cancellationToken)
    {
        RejectLink(directory);
        var path = Path.Combine(directory, "backup.json");
        RejectLink(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var record = await JsonSerializer.DeserializeAsync<ArchiveBackup>(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (record is null || record.SchemaVersion != 1 || record.Id != Path.GetFileName(directory) ||
            record.Length < 0 || record.Sha256 is not { Length: 64 } || !record.Sha256.All(Uri.IsHexDigit))
        {
            return null;
        }

        if (record.Label is { } label && (string.IsNullOrWhiteSpace(label) || label != label.Trim() || label.Any(char.IsControl)))
        {
            return null;
        }

        return record;
    }

    private static bool SameContent(ArchiveBackup a, ArchiveBackup b) => a with { Label = null } == b with { Label = null };

    private static bool Matches(ArchiveBackup backup, GameInstallation installation, SteamInstallState state) =>
        backup.InstallationPath == NormalizeRoot(installation) && backup.BuildId == state.BuildId && backup.DepotFingerprint == state.DepotFingerprint;

    internal FileStream AcquireLock(GameInstallation installation)
    {
        var directory = GetInstallationDirectory(installation);
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private string GetInstallationDirectory(GameInstallation installation) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRoot(installation)))));

    private string GetRecordDirectory(GameInstallation installation, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("Invalid backup identifier.");
        }

        return Path.Combine(GetInstallationDirectory(installation), id);
    }

    private static string NormalizeRoot(GameInstallation installation)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.RootDirectory));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Backup and restore cannot follow file or folder links.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
