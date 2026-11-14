using System.Security.Cryptography;
using System.Text.Json;
using SnowPatcher.Core.Packages;
using SnowPatcher.Core.Patching;

namespace SnowPatcher.Core.Updates;

internal static class UpdateFiles
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string In(string root, string relative)
    {
        PackagePath.Validate(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new IOException("An update path leaves the application folder.");
        }

        PatchStorage.RejectLinks(path);
        return path;
    }

    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions)
        ?? throw new InvalidDataException("The update metadata is empty.");

    internal static void Write<T>(string path, T value)
    {
        PatchStorage.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PatchStorage.SaveNew(temporary, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static void Copy(string source, string destination)
    {
        PatchStorage.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(source));
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static UpdateManifest Manifest(string directory)
    {
        var manifest = Read<UpdateManifest>(In(directory, UpdateManifest.FileName));
        if (manifest.FormatVersion != 1 || !SnowPatcherVersion.IsValid(manifest.Version) ||
            manifest.Platform is not ("win-x64" or "linux-x64") || manifest.Files is not { Length: > 0 })
        {
            throw new InvalidDataException("Unsupported SnowPatcher update metadata.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
            {
                throw new InvalidDataException("Invalid SnowPatcher update file list.");
            }

            _ = In(directory, file.Path);
            if (!names.Add(file.Path) || file.Path.Equals(UpdateManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                file.Size < 0 || file.Sha256 is null || !IsHash(file.Sha256))
            {
                throw new InvalidDataException("Invalid SnowPatcher update file list.");
            }
        }

        var executable = manifest.Platform == "win-x64" ? "SnowPatcher.exe" : "SnowPatcher";
        var legacyExecutable = manifest.Platform == "win-x64" ? "SnowPatcher.App.exe" : "SnowPatcher.App";
        var applicationPresent = names.Contains(executable) && names.Contains("SnowPatcher.dll") &&
            names.Contains("SnowPatcher.runtimeconfig.json") && names.Contains("SnowPatcher.deps.json");
        var legacyApplicationPresent = names.Contains(legacyExecutable) && names.Contains("SnowPatcher.App.dll") &&
            names.Contains("SnowPatcher.App.runtimeconfig.json") && names.Contains("SnowPatcher.App.deps.json");
        if ((!applicationPresent && !legacyApplicationPresent) || !names.Contains("SnowPatcher.Core.dll"))
        {
            throw new InvalidDataException("The update does not contain the SnowPatcher application.");
        }

        return manifest;
    }

    internal static void Verify(string directory, UpdateManifest manifest, CancellationToken token, bool exact = false)
    {
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = In(directory, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size || Hash(path) != file.Sha256)
            {
                throw new IOException("The SnowPatcher update file failed verification: " + file.Path);
            }
        }

        if (exact)
        {
            var expected = manifest.Files.Select(f => f.Path).Append(UpdateManifest.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                _ = In(directory, relative);
                if (!expected.Contains(relative))
                {
                    throw new IOException("The staged update contains an unlisted file: " + relative);
                }
            }
        }
    }

    internal static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
