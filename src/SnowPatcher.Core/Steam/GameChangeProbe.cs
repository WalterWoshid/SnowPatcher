using SnowPatcher.Core.Installations;

namespace SnowPatcher.Core.Steam;

public sealed record GameChangeSnapshot(string BuildId, string Key);

public static class GameChangeProbe
{
    public static GameChangeSnapshot Read(GameInstallation installation)
    {
        var state = GameInstallState.Read(installation);
        var paths = new[]
        {
            state.MetadataPath,
            state.ManifestPath,
            Path.Combine(installation.RootDirectory, "Sources/Bin/SnowRunner.exe"),
            Path.Combine(installation.RootDirectory, "preload/paks/client/initial.pak"),
            Path.Combine(installation.RootDirectory, "Sources/BinEditor/meta_desc_ssl.ps")
        }.OfType<string>().Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var stamps = paths.Select(p =>
        {
            var file = new FileInfo(p);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        });
        return new GameChangeSnapshot(state.BuildId, state.BuildId + ":" + state.Fingerprint + ":" + string.Join('|', stamps));
    }
}
