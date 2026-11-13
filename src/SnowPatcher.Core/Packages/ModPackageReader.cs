namespace SnowPatcher.Core.Packages;

public static class ModPackageReader
{
    public static ModPackage Read(string path, CancellationToken cancellationToken = default)
    {
        using var archive = new PackageArchive(path, cancellationToken);
        return Read(archive, cancellationToken);
    }

    internal static ModPackage Read(PackageArchive archive, CancellationToken cancellationToken)
    {
        if (!archive.Files.TryGetValue(PackageArchive.ManifestName, out var manifestEntry))
        {
            throw new InvalidDataException("This ZIP has no snowpatcher.json at its root. It is not a SnowPatcher package.");
        }

        var bytes = PackageArchive.ReadBytes(manifestEntry, manifestEntry.Length, null, true, cancellationToken);
        var manifest = PackageManifestReader.Read(bytes);
        if (archive.Files.Count != manifest.Files.Length + 1)
        {
            throw new InvalidDataException("The ZIP contents do not match the files listed in snowpatcher.json.");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!archive.Files.TryGetValue(file.Path, out var entry))
            {
                throw new InvalidDataException($"A listed payload is missing or has different letter casing: {file.Path}");
            }

            PackageArchive.ReadBytes(entry, file.Size, file.Sha256, false, cancellationToken);
        }
        archive.EnsureUnchanged(cancellationToken);
        return new ModPackage(archive.SourcePath, archive.Sha256, manifest);
    }

    internal static byte[] ReadPayload(ModPackage package, string path, CancellationToken cancellationToken)
    {
        var file = package.Manifest.Files.FirstOrDefault(x => x.Path == path)
            ?? throw new InvalidDataException("The requested payload is not listed in the validated package.");
        using var archive = new PackageArchive(package.SourcePath, cancellationToken);
        if (archive.Sha256 != package.ArchiveSha256)
        {
            throw new IOException("The mod ZIP changed since it was checked. Add it again before using its payloads.");
        }

        if (!archive.Files.TryGetValue(file.Path, out var entry))
        {
            throw new InvalidDataException("The package payload is missing.");
        }

        var data = PackageArchive.ReadBytes(entry, file.Size, file.Sha256, true, cancellationToken);
        archive.EnsureUnchanged(cancellationToken);
        return data;
    }
}
