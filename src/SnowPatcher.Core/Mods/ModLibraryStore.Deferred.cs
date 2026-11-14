using SnowPatcher.Core.Packages;
using SnowPatcher.Core.Patching;

namespace SnowPatcher.Core.Mods;

public sealed partial class ModLibraryStore
{
    public async Task<DeferredModArchive> DeferAsync(string sourcePath, string displayName, string requiredVersion,
        ModioSource? source = null, string? expectedSha256 = null, CancellationToken token = default)
    {
        if (_lock is null)
        {
            throw new IOException("The mod library is not open.");
        }

        var staging = Path.Combine(_root, "Staging");
        EnsureDirectory(staging);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await CopyToStagingAsync(sourcePath, temporary, token).ConfigureAwait(false);

            return await RetainDeferredAsync(temporary, displayName, requiredVersion, source, expectedSha256, token).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<DeferredModArchive> RetainDeferredAsync(string temporary, string displayName, string requiredVersion,
        ModioSource? source, string? expectedSha256, CancellationToken token)
    {
        if (new FileInfo(temporary).Length > PackageArchive.MaxArchiveSize)
        {
            throw new InvalidDataException("The ZIP exceeds the 2 GiB archive limit.");
        }

        var hash = await Task.Run(() => Updates.UpdateFiles.Hash(temporary), token).ConfigureAwait(false);
        if (expectedSha256 is not null && hash != expectedSha256)
        {
            throw new IOException("The ZIP changed before it could be saved for the application update.");
        }

        var directory = Path.Combine(_root, "Deferred");
        EnsureDirectory(directory);
        var path = Path.Combine(directory, hash + ".zip");
        PatchStorage.RejectLinks(path);
        token.ThrowIfCancellationRequested();
        if (File.Exists(path))
        {
            if (await Task.Run(() => Updates.UpdateFiles.Hash(path), token).ConfigureAwait(false) != hash)
            {
                throw new IOException("The saved mod ZIP changed. Download it again.");
            }
        }
        else
        {
            File.Move(temporary, path);
        }

        return new DeferredModArchive(path, hash, displayName, requiredVersion, source);
    }
}
