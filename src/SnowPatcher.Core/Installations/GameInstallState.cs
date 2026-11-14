using SnowPatcher.Core.Epic;
using SnowPatcher.Core.Steam;

namespace SnowPatcher.Core.Installations;

internal sealed record GameInstallState(GameStore Store, string BuildId, string Fingerprint, string MetadataPath,
    string? ManifestPath = null, string ContentPrefix = "", string? AppName = null, string? ManifestHash = null)
{
    public static GameInstallState Read(GameInstallation installation)
    {
        try
        { return ReadCore(installation); }
        catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The game's installation record is damaged or incomplete. Verify SnowRunner in its launcher, then check again.", error);
        }
    }

    private static GameInstallState ReadCore(GameInstallation installation)
    {
        var steamApps = Directory.GetParent(Path.TrimEndingDirectorySeparator(installation.RootDirectory))?.Parent;
        var receipt = steamApps is null ? null : Path.Combine(steamApps.FullName, $"appmanifest_{SteamInstallationLocator.AppId}.acf");
        if (receipt is not null && File.Exists(receipt) && SteamInstallState.OwnsReceipt(installation, receipt))
        {
            var steam = SteamInstallState.Read(installation);
            return new(GameStore.Steam, steam.BuildId, steam.DepotFingerprint, receipt);
        }

        return EpicInstallationLocator.ReadState(installation) ?? ManualInstallationState.Read(installation);
    }

    public void EnsureUnchanged(GameInstallation installation)
    {
        if (Read(installation) != this)
        {
            throw new IOException("The game installation changed during this operation. Wait for other file operations to finish, then check again.");
        }
    }
}
