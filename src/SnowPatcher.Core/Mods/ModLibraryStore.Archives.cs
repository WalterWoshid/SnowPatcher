using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Mods;

public sealed partial class ModLibraryStore
{
    public bool OwnsArchive(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(path);
        return full.StartsWith(Path.Combine(_root, "Local") + Path.DirectorySeparatorChar, comparison) || full.StartsWith(Path.Combine(_root, "Downloads") + Path.DirectorySeparatorChar, comparison);
    }

    public async Task<ModArchive> ImportAsync(string sourcePath, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        if (_lock is null)
        {
            throw new IOException("The mod library is not open.");
        }

        var staging = Path.Combine(_root, "Staging");
        var local = Path.Combine(_root, "Local");
        EnsureDirectory(staging);
        EnsureDirectory(local);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CopyToStagingAsync(sourcePath, temporary, cancellationToken).ConfigureAwait(false);

            var archive = await Task.Run(() => ModArchiveReader.Read(temporary, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (expectedSha256 is not null && archive.Sha256 != expectedSha256)
            {
                throw new InvalidDataException("The ZIP changed since it was added. The current library entry was kept.");
            }

            var directory = Path.Combine(local, archive.Sha256);
            EnsureDirectory(directory);
            var filename = Path.GetFileName(sourcePath);
            if (!filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                filename += ".zip";
            }

            var path = Path.Combine(directory, filename);
            foreach (var candidate in Directory.EnumerateFiles(directory, "*.zip"))
            {
                var existing = await ReadMatchingArchiveAsync(candidate, archive.Sha256, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }
            }
            return await StoreArchiveAsync(archive, temporary, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyToStagingAsync(string sourcePath, string temporary, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
        {
            const long limit = PackageArchive.MaxArchiveSize;
            if (input.Length > limit)
            {
                throw new InvalidDataException("The ZIP exceeds the 2 GiB archive limit.");
            }

            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > limit)
                {
                    throw new InvalidDataException("The ZIP exceeds the 2 GiB archive limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private static async Task<ModArchive?> ReadMatchingArchiveAsync(string path, string hash, CancellationToken cancellationToken)
    {
        Patching.PatchStorage.RejectLinks(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var archive = await Task.Run(() => ModArchiveReader.Read(path, cancellationToken), cancellationToken).ConfigureAwait(false);
            return archive.Sha256 == hash ? archive : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    private static async Task<ModArchive> StoreArchiveAsync(ModArchive archive, string temporary, string destination, CancellationToken cancellationToken)
    {
        var existing = await ReadMatchingArchiveAsync(destination, archive.Sha256, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destination))
        {
            var changed = Path.Combine(Path.GetDirectoryName(destination)!, $"changed-{Guid.NewGuid():N}.zip");
            File.Move(destination, changed);
            try
            { File.Move(temporary, destination); }
            catch { File.Move(changed, destination); throw; }
        }
        else
        {
            File.Move(temporary, destination);
        }

        return archive with
        {
            SourcePath = destination,
            Package = archive.Package is null ? null : new ModPackage(destination, archive.Sha256, archive.Package.Manifest)
        };
    }

    public async Task<IReadOnlyList<ModArchive>> ReadPreviousAsync(LibraryMod mod, CancellationToken cancellationToken = default)
    {
        var archives = new List<ModArchive>();
        var hashes = new HashSet<string>(StringComparer.Ordinal)
        {
            mod.Sha256
        };
        foreach (var path in mod.PreviousDownloads.Reverse().Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var archive = await Task.Run(() => ModArchiveReader.Read(path, cancellationToken), cancellationToken).ConfigureAwait(false);
                var folder = Path.GetFileName(Path.GetDirectoryName(path));
                var filename = Path.GetFileNameWithoutExtension(path);
                var expectedHash = folder?.Length == 64 && folder.All(Uri.IsHexDigit) ? folder : filename.Length > 65 && filename[^65] == '-' && filename[^64..].All(Uri.IsHexDigit) ? filename[^64..] : null;
                if (OwnsArchive(path) && expectedHash is not null && !string.Equals(archive.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mod.PackageId is not null && archive.Package?.Manifest.Id != mod.PackageId)
                {
                    continue;
                }

                if (hashes.Add(archive.Sha256))
                {
                    archives.Add(archive);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return archives;
    }
}
