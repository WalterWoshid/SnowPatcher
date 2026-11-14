namespace SnowPatcher.Core.Mods;

// Retained bytes are still untrusted. The updated reader must validate them before import.
public sealed record DeferredModArchive(string SourcePath, string Sha256, string DisplayName, string RequiredVersion, ModioSource? Source = null);
