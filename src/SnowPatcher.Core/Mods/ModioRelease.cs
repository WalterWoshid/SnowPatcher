namespace SnowPatcher.Core.Mods;

public sealed record ModioSource(long ModId, long FileId, string Name, string Version, long DateAdded, string Md5);

public sealed record ModioRelease(ModioSource Source, long Size, string? Sha256, Uri DownloadUrl);

public sealed record DownloadedMod(ModArchive Archive, ModioSource Source);
