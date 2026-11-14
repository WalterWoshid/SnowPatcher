using System.Security.Cryptography;
using System.Text;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Patching;

namespace SnowPatcher.Core.Updates;

public sealed record PendingModInstallation(string? GameFolder, DeferredModArchive[] Archives, bool Automatic = false, string? SelectionFingerprint = null);

public sealed class PendingModInstallStore
{
    private readonly string _path;

    internal PendingModInstallStore(string updates, string installation)
    {
        var identity = OperatingSystem.IsWindows() ? installation.ToUpperInvariant() : installation;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        _path = UpdateFiles.In(updates, "pending-" + key + ".json");
    }

    public PendingModInstallation? Read()
    {
        PatchStorage.RejectLinks(_path);
        if (!File.Exists(_path))
        {
            return null;
        }

        var request = UpdateFiles.Read<PendingModInstallation>(_path);
        if (request.Archives is not { Length: > 0 } || request.Archives.Any(a => a is null ||
            a.SourcePath is null || !Path.IsPathFullyQualified(a.SourcePath) || a.Sha256 is null || !UpdateFiles.IsHash(a.Sha256) ||
            string.IsNullOrWhiteSpace(a.DisplayName) || a.RequiredVersion is null || !SnowPatcherVersion.IsValid(a.RequiredVersion) ||
            (a.Source is { } source && (source.ModId <= 0 || source.FileId <= 0 || source.Md5?.Length != 32))) ||
            request.GameFolder is not null && !Path.IsPathFullyQualified(request.GameFolder) ||
            request.SelectionFingerprint is not null && !UpdateFiles.IsHash(request.SelectionFingerprint))
        {
            throw new InvalidDataException("The saved mod installation could not be read. Add the mod again.");
        }

        return request;
    }

    public void Save(string? gameFolder, IEnumerable<DeferredModArchive> archives, bool automatic = false, string? selectionFingerprint = null)
    {
        var request = new PendingModInstallation(string.IsNullOrEmpty(gameFolder) ? null : gameFolder, archives.DistinctBy(a => a.Sha256).ToArray(), automatic, selectionFingerprint);
        if (request.Archives.Length == 0)
        {
            return;
        }

        UpdateFiles.Write(_path, request);
    }

    public void Complete()
    {
        PatchStorage.RejectLinks(_path);
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
