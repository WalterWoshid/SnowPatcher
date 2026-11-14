using System.Text.Json;
using SnowPatcher.Core.Installations;

namespace SnowPatcher.Core.Epic;

public static partial class EpicInstallationLocator
{
    private static string ManifestsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static GameInstallation? Find(string? manifestsDirectory = null)
    {
        foreach (var path in ItemFiles(manifestsDirectory ?? ManifestsDirectory))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var item = document.RootElement;
                if (IsSnowRunner(item) && Text(item, "InstallLocation") is { Length: > 0 } folder && Path.IsPathFullyQualified(folder))
                {
                    var game = GameInstallationLocator.Resolve(folder);
                    if (game.HasInitialPak)
                    {
                        return game;
                    }
                }
            }
            catch (Exception error) when (Unreadable(error)) { }
        }

        foreach (var (_, item) in LauncherRecords(manifestsDirectory ?? ManifestsDirectory))
        {
            try
            {
                var folder = Text(item, "InstallLocation");
                if (IsSnowRunner(item) && Path.IsPathFullyQualified(folder) && GameInstallationLocator.Resolve(folder) is { HasInitialPak: true } game)
                {
                    return game;
                }
            }
            catch (Exception error) when (Unreadable(error)) { }
        }
        return null;
    }

    private static (string Path, JsonElement Item)[] LauncherRecords(string manifestsDirectory)
    {
        var epicRoot = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifestsDirectory)))?.Parent?.Parent;
        if (epicRoot is null)
        {
            return [];
        }
        var path = Path.Combine(epicRoot.FullName, "UnrealEngineLauncher", "LauncherInstalled.dat");
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("InstallationList", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(IsSnowRunner).Select(item => (path, item.Clone())).ToArray() : [];
        }
        catch (Exception error) when (Unreadable(error)) { return []; }
    }

    private static bool IsSnowRunner(JsonElement item) => item.ValueKind == JsonValueKind.Object && (Text(item, "AppName").Equals("Mayflower", StringComparison.OrdinalIgnoreCase) ||
        Text(item, "DisplayName").Equals("SnowRunner", StringComparison.OrdinalIgnoreCase) ||
        Text(item, "LaunchExecutable").Replace('\\', '/').EndsWith("Sources/Bin/SnowRunner.exe", StringComparison.OrdinalIgnoreCase));

    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static bool MatchesInstallation(string folder, GameInstallation game)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                return false;
            }
            if (SamePath(folder, game.RootDirectory))
            {
                return true;
            }
            // Only a content subfolder can belong to a registration at another path.
            var parent = Directory.GetParent(game.RootDirectory);
            return parent is not null && SamePath(folder, parent.FullName) &&
                SamePath(GameInstallationLocator.Resolve(folder).RootDirectory, game.RootDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static string[] ItemFiles(string directory) => Files(directory, "*.item");
    private static string[] Files(string directory, string pattern)
    {
        try
        { return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool Unreadable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
