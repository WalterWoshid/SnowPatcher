namespace SnowPatcher.Core.Steam;

public sealed record GameChangeSnapshot(string BuildId, string Key);

public static class GameChangeProbe
{
    public static GameChangeSnapshot Read(GameInstallation installation)
    {
        var state = SteamInstallState.Read(installation);
        var steamApps = Directory.GetParent(installation.RootDirectory)?.Parent?.FullName
            ?? throw new InvalidDataException("Steam's installation record is missing.");
        var paths = new[]
        {
            Path.Combine(steamApps, "appmanifest_1465360.acf"),
            Path.Combine(installation.RootDirectory, "preload/paks/client/initial.pak"),
            Path.Combine(installation.RootDirectory, "Sources/BinEditor/meta_desc_ssl.ps")
        };
        var stamps = paths.Select(p =>
        {
            var file = new FileInfo(p);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        });
        return new GameChangeSnapshot(state.BuildId, state.BuildId + ":" + state.DepotFingerprint + ":" + string.Join('|', stamps));
    }
}
