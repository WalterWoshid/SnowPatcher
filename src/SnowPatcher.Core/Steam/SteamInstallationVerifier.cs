using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using SteamKit2;

namespace SnowPatcher.Core.Steam;

public static class SteamInstallationVerifier
{
    private const string InitialPakRelativePath = "preload/paks/client/initial.pak";
    private const ulong FullyInstalled = 4;
    private const ulong StableStates = FullyInstalled | 2 | 64; // An update is available; the app is running.

    internal static void VerifyOriginals(GameInstallation installation, SteamInstallState state, IReadOnlyDictionary<string, byte[]?> originals)
    {
        state.EnsureUnchanged(installation);
        var steamApps = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.RootDirectory)))?.Parent
            ?? throw new InvalidDataException("Steam's installation record is missing.");
        var document = SteamKeyValues.Parse(File.ReadAllText(Path.Combine(steamApps.FullName, $"appmanifest_{SteamInstallationLocator.AppId}.acf")));
        var depots = document.Objects["AppState"].Objects["InstalledDepots"];
        var files = new Dictionary<string, List<DepotManifest.FileData>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, depot) in depots.Objects)
        {
            var manifest = FindCachedManifest(GetCacheDirectories(steamApps), uint.Parse(id, CultureInfo.InvariantCulture),
                ulong.Parse(depot.Values["manifest"], CultureInfo.InvariantCulture));
            if (manifest is null || manifest.FilenamesEncrypted || manifest.Files is null)
            {
                throw new InvalidDataException("Steam's reference data is missing. Verify the game files in Steam, then check again.");
            }

            foreach (var file in manifest.Files)
            {
                var name = file.FileName.Replace('\\', '/');
                if (!files.TryGetValue(name, out var list))
                {
                    files[name] = list = [];
                }

                list.Add(file);
            }
        }
        foreach (var (name, bytes) in originals)
        {
            if (!files.TryGetValue(name, out var expected))
            {
                if (bytes is null)
                {
                    continue;
                }

                throw new InvalidDataException($"{name} contains unmanaged changes. Keep your backup and restore the original before applying mods.");
            }
            if (bytes is null || expected.Any(f => f.FileHash.Length != 20 || f.TotalSize != (ulong)bytes.Length ||
                !f.FileHash.AsSpan().SequenceEqual(SteamChecksum.Compute(bytes))))
            {
                throw new InvalidDataException($"{name} does not match Steam's original. Keep your backup and restore the original before applying mods.");
            }
        }
        state.EnsureUnchanged(installation);
    }

    public static async Task<InstallationInspection> InspectAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        string? buildId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.RootDirectory));
            var steamApps = Directory.GetParent(root)?.Parent;
            if (steamApps is null)
            {
                return Unknown(null, "Steam's installation record could not be found for this folder.");
            }

            var appManifestPath = Path.Combine(steamApps.FullName, $"appmanifest_{SteamInstallationLocator.AppId}.acf");
            if (!File.Exists(appManifestPath))
            {
                return Unknown(null, "Steam's installation record could not be found for this folder.");
            }

            var appManifestText = await File.ReadAllTextAsync(appManifestPath, cancellationToken).ConfigureAwait(false);
            var document = SteamKeyValues.Parse(appManifestText);
            if (!document.Objects.TryGetValue("AppState", out var app) ||
                app.Values.GetValueOrDefault("appid") != SteamInstallationLocator.AppId ||
                !app.Values.TryGetValue("installdir", out var installDirectory))
            {
                return Unknown(null, "Steam's installation record is incomplete.");
            }

            var recordedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(steamApps.FullName, "common", installDirectory)));
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(root, recordedRoot, pathComparison))
            {
                return Unknown(null, "Steam's installation record belongs to a different game folder.");
            }

            buildId = app.Values.GetValueOrDefault("buildid");
            if (!ulong.TryParse(buildId, NumberStyles.None, CultureInfo.InvariantCulture, out var build) || build == 0)
            {
                return Unknown(null, "Steam has not recorded an installed build.");
            }

            if (!ulong.TryParse(app.Values.GetValueOrDefault("StateFlags"), NumberStyles.None, CultureInfo.InvariantCulture, out var state) ||
                (state & FullyInstalled) == 0 || (state & ~StableStates) != 0)
            {
                return Unknown(buildId, "Steam is updating or checking this installation. Verification can run after it finishes.");
            }

            if (!app.Objects.TryGetValue("InstalledDepots", out var depots) || depots.Objects.Count == 0)
            {
                return Unknown(buildId, "Steam's installed depot information is missing.");
            }

            var cacheDirectories = GetCacheDirectories(steamApps);
            var candidates = new List<DepotManifest.FileData>();

            foreach (var (depotName, depot) in depots.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!uint.TryParse(depotName, NumberStyles.None, CultureInfo.InvariantCulture, out var depotId) ||
                    !ulong.TryParse(depot.Values.GetValueOrDefault("manifest"), NumberStyles.None, CultureInfo.InvariantCulture, out var manifestId) ||
                    depotId == 0 || manifestId == 0)
                {
                    return Unknown(buildId, "Steam's installed depot information is incomplete.");
                }

                var manifest = FindCachedManifest(cacheDirectories, depotId, manifestId);
                if (manifest is null)
                {
                    return Unknown(buildId, "Steam's reference data for this build is missing or unreadable. Use Steam's file verification to refresh it.");
                }

                if (manifest.Files is null)
                {
                    return Unknown(buildId, "Steam's reference manifest has no file records.");
                }

                candidates.AddRange(manifest.Files.Where(file => IsInitialPak(file, manifest.FilenamesEncrypted)));
            }

            if (candidates.Count == 0 || candidates.Any(file => file.FileHash.Length != 20))
            {
                return Unknown(buildId, "Steam's reference data does not identify initial.pak.");
            }

            var expected = candidates[0];
            if (candidates.Any(file => file.TotalSize != expected.TotalSize || !file.FileHash.AsSpan().SequenceEqual(expected.FileHash)))
            {
                return Unknown(buildId, "Steam lists more than one original version of initial.pak for this installation.");
            }

            var before = new FileInfo(installation.InitialPakPath);
            if (!before.Exists)
            {
                return Unknown(buildId, "initial.pak could not be found in the selected folder.");
            }

            var length = before.Length;
            var lastWrite = before.LastWriteTimeUtc;

            await using var stream = new FileStream(installation.InitialPakPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = await SteamChecksum.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);

            before.Refresh();
            if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != lastWrite || stream.Length != length ||
                await File.ReadAllTextAsync(appManifestPath, cancellationToken).ConfigureAwait(false) != appManifestText)
            {
                return Unknown(buildId, "The installation changed while it was being checked. Select the folder again to retry.");
            }

            var original = (ulong)length == expected.TotalSize && actualHash.AsSpan().SequenceEqual(expected.FileHash);
            return new InstallationInspection(buildId,
                original ? FileVerificationState.Original : FileVerificationState.Changed,
                original
                    ? "initial.pak matches Steam's original file for this installed build."
                    : "initial.pak differs from Steam's original file. This can be caused by installed mods or damaged files.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                      FormatException or ArgumentException or NotSupportedException or SecurityException)
        {
            return Unknown(buildId, "The game file or Steam's reference data could not be read. No files were changed.");
        }
    }

    private static HashSet<string> GetCacheDirectories(DirectoryInfo steamApps)
    {
        var roots = SteamInstallationLocator.GetSteamRoots();
        if (steamApps.Parent is { } library)
        {
            roots.Add(library.FullName);
        }

        var caches = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots)
        {
            caches.Add(Path.Combine(root, "depotcache"));
            caches.Add(Path.Combine(root, "steamapps", "depotcache"));
        }

        return caches;
    }

    private static DepotManifest? FindCachedManifest(IEnumerable<string> cacheDirectories, uint depotId, ulong manifestId)
    {
        foreach (var directory in cacheDirectories)
        {
            var path = Path.Combine(directory, $"{depotId}_{manifestId}.manifest");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 64 * 1024 * 1024)
                {
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var manifest = DepotManifest.Deserialize(stream);
                if (manifest.DepotID == depotId && manifest.ManifestGID == manifestId)
                {
                    return manifest;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                          InvalidOperationException or ArgumentException or OverflowException or ProtoBuf.ProtoException)
            {
                // Another Steam library may hold a readable copy of the same manifest.
            }
        }

        return null;
    }

    private static bool IsInitialPak(DepotManifest.FileData file, bool filenamesEncrypted)
    {
        if (!filenamesEncrypted)
        {
            return string.Equals(file.FileName.Replace('\\', '/'), InitialPakRelativePath, StringComparison.OrdinalIgnoreCase);
        }

        // Steam also records the SHA-1 of the plain filename, separately from its encrypted name.
        foreach (var path in new[] { InitialPakRelativePath, InitialPakRelativePath.Replace('/', '\\') })
        {
            if (SteamChecksum.Compute(Encoding.UTF8.GetBytes(path)).AsSpan().SequenceEqual(file.FileNameHash))
            {
                return true;
            }
        }

        return false;
    }

    private static InstallationInspection Unknown(string? buildId, string detail) =>
        new(buildId, FileVerificationState.UnableToVerify, detail);
}
