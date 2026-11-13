using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Mods;

public sealed partial class ModLibraryStore
{
    public async Task<DownloadedMod> DownloadAsync(ModioClient client, ModioRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_lock is null)
        {
            throw new IOException("The mod library is not open.");
        }

        var staging = Path.Combine(_root, "Staging");
        var downloads = Path.Combine(_root, "Downloads", release.Source.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        EnsureDirectory(_root);
        EnsureDirectory(staging);
        EnsureDirectory(Path.Combine(_root, "Downloads"));
        EnsureDirectory(downloads);
        var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var hash = await client.DownloadAsync(release, temporary, progress, cancellationToken).ConfigureAwait(false);
            var archive = await Task.Run(() => ModArchiveReader.Read(temporary, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (archive.Sha256 != hash)
            {
                throw new InvalidDataException("The ZIP changed during validation.");
            }

            var path = Path.Combine(downloads, $"{release.Source.FileId}-{hash}.zip");
            var stored = await StoreArchiveAsync(archive, temporary, path, cancellationToken).ConfigureAwait(false);
            return new DownloadedMod(stored, release.Source);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
