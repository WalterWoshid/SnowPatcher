using System.Collections.Immutable;
using System.Text.Json;

namespace SnowPatcher.Core.Packages;

// A validated package snapshot, not a statement of game compatibility.
public sealed class ModPackage
{
    internal ModPackage(string sourcePath, string archiveSha256, PackageManifest manifest)
    {
        SourcePath = sourcePath;
        ArchiveSha256 = archiveSha256;
        Manifest = manifest;
    }

    public string SourcePath
    { get; }
    public string ArchiveSha256
    { get; }
    public PackageManifest Manifest
    { get; }

    public byte[] ReadPayload(string path, CancellationToken cancellationToken = default) =>
        ModPackageReader.ReadPayload(this, path, cancellationToken);
}

public sealed record PackageManifest(
    string Id,
    string Name,
    string Version,
    ImmutableArray<string> Authors,
    string? Description,
    ImmutableArray<string> TestedBuilds,
    ImmutableArray<PackageDependency> Dependencies,
    ImmutableArray<string> Conflicts,
    ImmutableArray<PackageCheck> Checks,
    ImmutableArray<PackageFile> Files,
    ImmutableArray<PackageOperation> Operations,
    JsonElement? Relocations = null,
    string? ModioUrl = null);

public sealed record PackageDependency(string Id, string MinVersion, string? MaxVersionExclusive);
public sealed record PackageFile(string Path, string Sha256, long Size);
public sealed record PackageMember(string Format, string Name);
public sealed record PackageTarget(string File, ImmutableArray<PackageMember> Members);
public sealed record PackageCheck(PackageTarget Target, string Sha256);

// Keep the checked, immutable JSON for the adapters; never execute package code.
public sealed record PackageOperation(string Id, string Type, PackageTarget Target, JsonElement Definition);
