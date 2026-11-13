using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SnowPatcher.Core.Steam;

internal sealed record SteamInstallState(string BuildId, string DepotFingerprint)
{
    public static SteamInstallState Read(GameInstallation installation)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.RootDirectory));
        var steamApps = Directory.GetParent(root)?.Parent?.FullName
            ?? throw new InvalidDataException("Steam's installation record is missing.");
        var document = SteamKeyValues.Parse(File.ReadAllText(Path.Combine(steamApps, $"appmanifest_{SteamInstallationLocator.AppId}.acf")));
        if (!document.Objects.TryGetValue("AppState", out var app) ||
            app.Values.GetValueOrDefault("appid") != SteamInstallationLocator.AppId ||
            !app.Values.TryGetValue("installdir", out var directory))
        {
            throw new InvalidDataException("Steam's installation record is incomplete.");
        }

        var recorded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(steamApps, "common", directory)));
        if (!string.Equals(root, recorded, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Steam's installation record belongs to another folder.");
        }

        if (!ulong.TryParse(app.Values.GetValueOrDefault("StateFlags"), out var flags) ||
            (flags & 4) == 0 || (flags & ~(2UL | 4 | 64)) != 0)
        {
            throw new InvalidOperationException("Wait for Steam to finish updating or checking the game.");
        }

        if (!ulong.TryParse(app.Values.GetValueOrDefault("buildid"), out var build) || build == 0 ||
            !app.Objects.TryGetValue("InstalledDepots", out var depots) || depots.Objects.Count == 0)
        {
            throw new InvalidDataException("Steam's build information is missing.");
        }

        var entries = new List<string>();
        foreach (var (id, depot) in depots.Objects)
        {
            if (!uint.TryParse(id, out var depotId) || depotId == 0 ||
                !ulong.TryParse(depot.Values.GetValueOrDefault("manifest"), out var manifest) || manifest == 0)
            {
                throw new InvalidDataException("Steam's depot information is incomplete.");
            }

            entries.Add(FormattableString.Invariant($"{depotId}:{manifest}"));
        }

        entries.Sort(StringComparer.Ordinal);
        return new SteamInstallState(build.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)))));
    }

    public void EnsureUnchanged(GameInstallation installation)
    {
        if (Read(installation) != this)
        {
            throw new IOException("The game build changed during this operation. Reopen SnowPatcher after Steam finishes.");
        }
    }
}
