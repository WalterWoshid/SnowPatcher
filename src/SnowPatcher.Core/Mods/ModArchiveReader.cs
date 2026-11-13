using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Mods;

public static class ModArchiveReader
{
    public static ModArchive Read(string path, CancellationToken cancellationToken = default)
    {
        using var archive = new PackageArchive(path, cancellationToken);
        var package = archive.HasManifest ? ModPackageReader.Read(archive, cancellationToken) : null;
        if (package is null)
        {
            archive.EnsureUnchanged(cancellationToken);
        }

        return new ModArchive(archive.SourcePath, archive.Files.Count, package, archive.Sha256);
    }
}
