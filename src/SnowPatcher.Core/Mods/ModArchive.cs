using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Mods;

// Package validation checks the download, not its compatibility with an installed game.
public sealed record ModArchive(string SourcePath, int FileCount, ModPackage? Package = null, string Sha256 = "")
{
    public string DisplayName => Package?.Manifest.Name ?? Path.GetFileNameWithoutExtension(SourcePath);
}
