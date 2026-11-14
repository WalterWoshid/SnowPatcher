using System.Formats.Tar;
using System.IO.Compression;

namespace SnowPatcher.Core.Updates;

internal static class UpdateArchive
{
    internal static void Extract(string archive, string destination, string platform, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (platform == "win-x64")
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var kind = (entry.ExternalAttributes >> 16) & 0xf000;
                if (kind is not (0 or 0x4000 or 0x8000))
                {
                    throw new IOException("Update archives cannot contain links or special files.");
                }

                var path = EntryPath(entry.FullName, entry.FullName.EndsWith('/'), destination, names);
                if (path is null)
                {
                    continue;
                }

                using var input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew);
                input.CopyTo(output);
            }
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                token.ThrowIfCancellationRequested();
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                {
                    throw new IOException("Update archives cannot contain links or special files.");
                }

                var path = EntryPath(entry.Name, entry.EntryType == TarEntryType.Directory, destination, names);
                if (path is null)
                {
                    continue;
                }

                using (var output = new FileStream(path, FileMode.CreateNew))
                {
                    entry.DataStream?.CopyTo(output);
                }
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0x1ff);
                }
            }
        }
    }

    private static string? EntryPath(string name, bool directory, string destination, HashSet<string> names)
    {
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        name = directory ? name.TrimEnd('/') : name;
        if (directory && name.Length == 0)
        {
            return null;
        }

        var path = UpdateFiles.In(destination, name);
        if (!names.Add(name))
        {
            throw new IOException("The update archive contains duplicate paths.");
        }

        if (directory)
        {
            Directory.CreateDirectory(path);
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
