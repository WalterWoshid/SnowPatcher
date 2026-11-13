using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace SnowPatcher.Core.Steam;

public static class SteamInstallationLocator
{
    internal const string AppId = "1465360";

    public static GameInstallation? Find()
    {
        var libraries = new HashSet<string>(PathComparer);
        foreach (var steamRoot in GetSteamRoots())
        {
            libraries.Add(steamRoot);
            foreach (var configFolder in new[] { "steamapps", "config" })
            {
                var config = ReadManifest(Path.Combine(steamRoot, configFolder, "libraryfolders.vdf"));
                if (config is null || !config.Objects.TryGetValue("libraryfolders", out var folders))
                {
                    continue;
                }

                foreach (var (index, folder) in folders.Objects)
                {
                    if (int.TryParse(index, out _) && folder.Values.TryGetValue("path", out var path))
                    {
                        AddPath(libraries, path);
                    }
                }

                // Older Steam clients stored library paths directly under numbered keys.
                foreach (var (index, path) in folders.Values)
                {
                    if (int.TryParse(index, out _))
                    {
                        AddPath(libraries, path);
                    }
                }
            }
        }

        foreach (var library in libraries)
        {
            var common = Path.Combine(library, "steamapps", "common");
            var manifest = ReadManifest(Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf"));

            if (manifest is not null && manifest.Objects.TryGetValue("AppState", out var app) &&
                app.Values.GetValueOrDefault("appid") == AppId &&
                app.Values.TryGetValue("installdir", out var installDirectory))
            {
                var path = NormalizePath(Path.Combine(common, installDirectory));
                if (path is not null && path.StartsWith(common + Path.DirectorySeparatorChar, PathComparison))
                {
                    var installation = new GameInstallation(path);
                    if (installation.HasInitialPak)
                    {
                        return installation;
                    }
                }
            }

            var conventional = new GameInstallation(Path.Combine(common, "SnowRunner"));
            if (conventional.HasInitialPak)
            {
                return conventional;
            }
        }

        return null;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static HashSet<string> GetSteamRoots()
    {
        var roots = new HashSet<string>(PathComparer);
        AddPath(roots, Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH"));

        if (OperatingSystem.IsWindows())
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                AddPath(roots, ReadSteamRegistry(RegistryHive.CurrentUser, view, "SteamPath"));
                AddPath(roots, ReadSteamRegistry(RegistryHive.LocalMachine, view, "InstallPath"));
            }

            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                var programFiles = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(programFiles))
                {
                    AddPath(roots, Path.Combine(programFiles, "Steam"));
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(dataHome))
            {
                AddPath(roots, Path.Combine(dataHome, "Steam"));
            }

            if (!string.IsNullOrEmpty(home))
            {
                foreach (var relative in new[]
                {
                    ".local/share/Steam", ".steam/steam", ".steam/root",
                    ".var/app/com.valvesoftware.Steam/.local/share/Steam",
                    ".var/app/com.valvesoftware.Steam/data/Steam",
                    ".var/app/com.valvesoftware.Steam/.steam/steam"
                })
                {
                    AddPath(roots, Path.Combine(home, relative));
                }
            }
        }

        return roots;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadSteamRegistry(RegistryHive hive, RegistryView view, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue(valueName) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static void AddPath(HashSet<string> paths, string? path)
    {
        if (NormalizePath(path) is { } normalized)
        {
            paths.Add(normalized);
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    private static SteamKeyValues? ReadManifest(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 8 * 1024 * 1024)
            {
                return null;
            }

            return SteamKeyValues.Parse(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or SecurityException)
        {
            return null;
        }
    }
}
