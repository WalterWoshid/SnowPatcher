using SnowPatcher.Core.Epic;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Installations;

public static class GameInstallationLocator
{
    public static GameInstallation? Find() => SteamInstallationLocator.Find() ?? EpicInstallationLocator.Find();

    public static GameInstallation Resolve(string path)
    {
        var installation = new GameInstallation(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        if (installation.HasInitialPak)
        {
            return installation;
        }

        var localized = new GameInstallation(Path.Combine(installation.RootDirectory, "en_us"));
        if (localized.HasInitialPak)
        {
            return localized;
        }

        var recoverable = HasContentDirectory(installation) ? installation : HasContentDirectory(localized) ? localized : null;
        if (Directory.Exists(installation.RootDirectory))
        {
            foreach (var child in Directory.EnumerateDirectories(installation.RootDirectory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = new GameInstallation(child);
                if (candidate.HasInitialPak)
                {
                    return candidate;
                }
                if (recoverable is null && HasContentDirectory(candidate))
                {
                    recoverable = candidate;
                }
            }
        }
        return recoverable ?? installation;
    }

    private static bool HasContentDirectory(GameInstallation installation) => Directory.Exists(Path.GetDirectoryName(installation.InitialPakPath));
}
