using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnowPatcher.Core.Installations;

namespace SnowPatcher.Core.Epic;

public static partial class EpicInstallationLocator
{
    internal static GameInstallState? ReadState(GameInstallation game, string? manifestsDirectory = null)
    {
        try
        { return ReadStateCore(game, manifestsDirectory ?? ManifestsDirectory); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Epic's installation record is damaged or incomplete. Verify SnowRunner in Epic Games, then check again.", error);
        }
    }

    private static GameInstallState? ReadStateCore(GameInstallation game, string manifestsDirectory)
    {
        foreach (var path in ItemFiles(manifestsDirectory))
        {
            JsonDocument document;
            try
            { document = JsonDocument.Parse(File.ReadAllBytes(path)); }
            catch (Exception error) when (Unreadable(error))
            {
                continue;
            }
            using (document)
            {
                var item = document.RootElement;
                var folder = Text(item, "InstallLocation");
                if (!IsSnowRunner(item) || !MatchesInstallation(folder, game))
                {
                    continue;
                }

                if (Flag(item, "bIsIncompleteInstall") || Flag(item, "bNeedsValidation"))
                {
                    throw new InvalidOperationException("Wait for Epic Games to finish installing or verifying SnowRunner.");
                }
                var directory = Path.Combine(folder, ".egstore");
                EnsureStable(directory);
                var location = Text(item, "ManifestLocation");
                var manifest = Path.IsPathFullyQualified(location) ? FindManifest(location, Text(item, "InstallationGuid")) : null;
                manifest ??= FindManifest(directory, Text(item, "InstallationGuid"));
                var version = Text(item, "AppVersionString");
                if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsControl))
                {
                    throw new InvalidDataException("Epic Games has not recorded the installed SnowRunner version.");
                }

                var appName = Text(item, "AppName");
                var manifestHash = Text(item, "ManifestHash");
                if (manifestHash.Length == 0 && manifest is not null)
                {
                    manifestHash = EpicManifest.Read(manifest).FileHash;
                }
                return CreateState(game, path, folder, appName, version, manifest, manifestHash);
            }
        }

        foreach (var (receipt, item) in LauncherRecords(manifestsDirectory))
        {
            var folder = Text(item, "InstallLocation");
            if (!MatchesInstallation(folder, game))
            {
                continue;
            }
            var directory = Path.Combine(folder, ".egstore");
            EnsureStable(directory);
            var version = Text(item, "AppVersion");
            foreach (var manifest in Files(directory, "*.manifest"))
            {
                EpicManifest reference;
                try
                { reference = EpicManifest.Read(manifest); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                if (reference.AppName == Text(item, "AppName") && reference.BuildVersion == version)
                {
                    EnsureRegisteredRecordReadable(manifestsDirectory, manifest);
                    return CreateState(game, receipt, folder, reference.AppName, version, manifest, reference.FileHash);
                }
            }
            throw new InvalidDataException("Epic's installation and local manifest do not match. Verify SnowRunner in Epic Games, then check again.");
        }

        // Local receipts also support moved installations and launcher metadata under Wine.
        foreach (var root in new[] { game.RootDirectory, Directory.GetParent(game.RootDirectory)?.FullName }.OfType<string>())
        {
            var directory = Path.Combine(root, ".egstore");
            foreach (var receipt in Files(directory, "*.mancpn"))
            {
                JsonDocument document;
                try
                { document = JsonDocument.Parse(File.ReadAllBytes(receipt)); }
                catch (Exception error) when (Unreadable(error)) { continue; }
                using var localRecord = document;
                if (!IsSnowRunner(localRecord.RootElement))
                {
                    continue;
                }
                EnsureRegisteredRecordReadable(manifestsDirectory, receipt);
                EnsureStable(directory);
                var manifest = FindManifest(directory, Path.GetFileNameWithoutExtension(receipt));
                if (manifest is null)
                {
                    // Copied installations may retain a local receipt without reference data.
                    continue;
                }
                var reference = EpicManifest.Read(manifest);
                if (!string.Equals(reference.AppName, Text(localRecord.RootElement, "AppName"), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Epic's game manifest belongs to another installation.");
                }
                return CreateState(game, receipt, root, reference.AppName, reference.BuildVersion, manifest, reference.FileHash);
            }
        }

        return null;
    }

    private static bool Flag(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return false;
        }
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
            : throw new InvalidDataException("Epic's installation state is incomplete. Wait for Epic Games to finish, then check again.");
    }

    private static string Prefix(string folder, string content)
    {
        var relative = Path.GetRelativePath(folder, content).Replace('\\', '/');
        return relative == "." ? "" : relative + "/";
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static GameInstallState CreateState(GameInstallation game, string receipt, string folder, string appName, string version,
        string? manifest, string manifestHash)
    {
        manifestHash = manifestHash.ToUpperInvariant();
        return new(GameStore.Epic, version, Hash(string.Join('\n', "epic", appName, version, manifestHash)),
            Path.GetFullPath(receipt), manifest, Prefix(folder, game.RootDirectory), appName, manifestHash);
    }

    private static void EnsureRegisteredRecordReadable(string manifestsDirectory, string receipt)
    {
        var path = Path.Combine(manifestsDirectory, Path.GetFileNameWithoutExtension(receipt) + ".item");
        if (!File.Exists(path))
        {
            return;
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Epic's installation record is incomplete. Wait for Epic Games to finish, then check again.");
        }
    }

    private static string? FindManifest(string directory, string id) => !string.IsNullOrEmpty(id) && Path.GetFileName(id) == id &&
        File.Exists(Path.Combine(directory, id + ".manifest")) ? Path.Combine(directory, id + ".manifest") : null;

    private static void EnsureStable(string directory)
    {
        try
        {
            if (Directory.EnumerateFiles(directory, "*.resume").Any())
            {
                throw new InvalidOperationException("Wait for Epic Games to finish updating or verifying SnowRunner.");
            }
        }
        catch (DirectoryNotFoundException) { }
    }
}
