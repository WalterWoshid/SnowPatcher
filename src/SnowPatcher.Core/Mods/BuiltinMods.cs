using System.IO.Compression;
using System.Security.Cryptography;

namespace SnowPatcher.Core.Mods;

public static class BuiltinMods
{
    public const string IndicatorId = "snowpatcher-indicator";
    private static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowPatcher", "Builtin");

    public static ModArchive EnsureIndicator(string? directory = null)
    {
        directory = Path.GetFullPath(directory ?? DefaultDirectory);
        Patching.PatchStorage.RejectLinks(directory);
        using var manifest = typeof(BuiltinMods).Assembly.GetManifestResourceStream("SnowPatcher.Indicator.json")
            ?? throw new InvalidDataException("The bundled indicator package is missing.");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("snowpatcher.json", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            manifest.CopyTo(stream);
        }
        var bytes = output.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = Path.Combine(directory, $"SnowPatcherIndicator-{hash}.zip");
        Directory.CreateDirectory(directory);
        Patching.PatchStorage.RejectLinks(path);
        if (!File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Patching.PatchStorage.SaveNew(temp, bytes);
                File.Move(temp, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        var archive = ModArchiveReader.Read(path);
        if (archive.Sha256 != hash || archive.Package?.Manifest.Id != IndicatorId)
        {
            throw new InvalidDataException("The bundled indicator ZIP changed. Its file was kept.");
        }

        return archive;
    }

    public static bool OwnsArchive(string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(DefaultDirectory) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
