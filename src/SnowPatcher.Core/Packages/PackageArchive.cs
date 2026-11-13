using System.IO.Compression;
using System.Security.Cryptography;

namespace SnowPatcher.Core.Packages;

internal sealed class PackageArchive : IDisposable
{
    internal const string ManifestName = "snowpatcher.json";
    internal const int MaxManifestSize = 4 * 1024 * 1024;
    internal const int MaxPayloadSize = 256 * 1024 * 1024;
    internal const long MaxExpandedSize = 1024L * 1024 * 1024;
    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    public string SourcePath
    { get; }
    public string Sha256
    { get; }
    public Dictionary<string, ZipArchiveEntry> Files { get; } = new(StringComparer.Ordinal);
    public bool HasManifest => Files.ContainsKey(ManifestName);

    public PackageArchive(string path, CancellationToken cancellationToken)
    {
        SourcePath = Path.GetFullPath(path);
        _stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (_stream.Length > 2L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("The ZIP exceeds the 2 GiB archive limit.");
            }

            Sha256 = HashArchive(cancellationToken);
            _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                Index(cancellationToken);
            }
            catch { _archive.Dispose(); throw; }
        }
        catch { _stream.Dispose(); throw; }
    }

    private void Index(CancellationToken cancellationToken)
    {
        if (_archive.Entries.Count > 100000)
        {
            throw new InvalidDataException("The ZIP contains too many entries.");
        }

        var paths = new Dictionary<string, (string Spelling, bool Directory, bool Explicit)>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in _archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var name = directory ? entry.FullName[..^1] : entry.FullName;
            PackagePath.Validate(name);
            var attributes = unchecked((uint)entry.ExternalAttributes);
            var unixType = (attributes >> 16) & 0xf000;
            if ((attributes & 0x400) != 0 || unixType is not (0 or 0x8000 or 0x4000) ||
                (unixType == 0x4000 && !directory) || (unixType == 0x8000 && directory) ||
                ((attributes & 0x10) != 0 && !directory))
            {
                throw new InvalidDataException("The ZIP contains a link, special file or inconsistent directory entry.");
            }

            Register(name, directory, true);
            var parent = name;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                Register(parent, true, false);
            }

            if (directory)
            {
                if (entry.Length != 0)
                {
                    throw new InvalidDataException("A ZIP directory contains unexpected data.");
                }

                continue;
            }
            var limit = name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ? MaxManifestSize : MaxPayloadSize;
            if (entry.Length > limit)
            {
                throw new InvalidDataException($"ZIP member exceeds its size limit: {name}");
            }

            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedSize + MaxManifestSize)
            {
                throw new InvalidDataException("The ZIP exceeds the expanded-data limit.");
            }

            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) && name != ManifestName)
            {
                throw new InvalidDataException("The manifest must be named snowpatcher.json, using lowercase, at the ZIP root.");
            }

            Files.Add(name, entry);
        }
        if (Files.Count == 0)
        {
            throw new InvalidDataException("The ZIP contains no files.");
        }

        void Register(string name, bool directory, bool explicitEntry)
        {
            if (paths.TryGetValue(name, out var prior))
            {
                if (prior.Spelling != name || prior.Directory != directory || (prior.Explicit && explicitEntry))
                {
                    throw new InvalidDataException("The ZIP contains duplicate paths, case aliases or a file/folder collision.");
                }

                if (explicitEntry)
                {
                    paths[name] = (name, directory, true);
                }
            }
            else
            {
                if (paths.Count >= 200000)
                {
                    throw new InvalidDataException("The ZIP contains too many folder paths.");
                }

                paths.Add(name, (name, directory, explicitEntry));
            }
        }
    }

    public void EnsureUnchanged(CancellationToken cancellationToken)
    {
        if (HashArchive(cancellationToken) != Sha256)
        {
            throw new IOException("The mod ZIP changed while it was being read. Add it again.");
        }
    }

    private string HashArchive(CancellationToken cancellationToken)
    {
        _stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = _stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static byte[] ReadBytes(ZipArchiveEntry entry, long expectedSize, string? expectedHash, bool keepBytes,
        CancellationToken cancellationToken)
    {
        if (expectedSize < 0 || expectedSize > MaxPayloadSize || entry.Length != expectedSize)
        {
            throw new InvalidDataException($"Payload size does not match: {entry.FullName}");
        }

        using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var result = keepBytes ? new byte[checked((int)expectedSize)] : [];
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > expectedSize - total)
            {
                throw new InvalidDataException($"Payload expands beyond its declared size: {entry.FullName}");
            }

            hash.AppendData(buffer, 0, count);
            if (keepBytes)
            {
                buffer.AsSpan(0, count).CopyTo(result.AsSpan(checked((int)total)));
            }

            total += count;
        }
        if (total != expectedSize || (expectedHash is not null && Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedHash))
        {
            throw new InvalidDataException($"Payload integrity check failed: {entry.FullName}");
        }

        return result;
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }
}
