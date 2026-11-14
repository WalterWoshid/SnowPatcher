using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using SnowPatcher.Core.Installations;

namespace SnowPatcher.Core.Epic;

internal static class EpicInstallationVerifier
{
    public static async Task<InstallationInspection> InspectAsync(GameInstallation game, GameInstallState state, CancellationToken token)
    {
        try
        {
            var expected = Reference(state, "preload/paks/client/initial.pak");
            await using var stream = new FileStream(game.InitialPakPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var before = new FileInfo(game.InitialPakPath);
            var length = before.Length;
            var writeTime = before.LastWriteTimeUtc;
            var hash = await ChecksumAsync(stream, token).ConfigureAwait(false);
            before.Refresh();
            state.EnsureUnchanged(game);
            if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != writeTime)
            {
                throw new IOException("The game archive changed during verification. Check again after Epic Games finishes.");
            }
            var original = hash.AsSpan().SequenceEqual(expected);
            return new(state.BuildId, original ? FileVerificationState.Original : FileVerificationState.Changed,
                original ? "initial.pak matches Epic's original for this installed version." : "initial.pak differs from Epic's original for this installed version.", Store: GameStore.Epic);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(state.BuildId, FileVerificationState.UnableToVerify, error.Message, Store: GameStore.Epic);
        }
    }

    public static void VerifyOriginals(GameInstallState state, IReadOnlyDictionary<string, byte[]?> files)
    {
        if (files.Count == 0)
        {
            return;
        }
        var manifest = ReadReference(state);
        foreach (var (path, bytes) in files)
        {
            var expected = LookupFile(manifest, state, path, allowMissing: bytes is null);
            if (bytes is null && expected.Length == 0)
            {
                continue;
            }
            if (bytes is null || !EpicManifest.Checksum(bytes).AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException($"{path} does not match Epic's original. Keep your backup and verify SnowRunner in Epic Games before applying mods.");
            }
        }
    }

    private static byte[] Reference(GameInstallState state, string relative, bool allowMissing = false) =>
        LookupFile(ReadReference(state), state, relative, allowMissing);

    private static EpicManifest ReadReference(GameInstallState state)
    {
        if (state.ManifestPath is null)
        {
            throw new InvalidDataException("Epic's reference manifest is missing. Verify SnowRunner in Epic Games to refresh it.");
        }
        var manifest = EpicManifest.Read(state.ManifestPath);
        if (manifest.BuildVersion != state.BuildId || manifest.AppName != state.AppName)
        {
            throw new InvalidDataException("Epic's reference manifest belongs to another game version. Wait for Epic Games to finish, then check again.");
        }
        if (!string.IsNullOrEmpty(state.ManifestHash) && !manifest.FileHash.Equals(state.ManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Epic's reference manifest does not match its installation record. Verify SnowRunner in Epic Games, then check again.");
        }
        return manifest;
    }

    private static byte[] LookupFile(EpicManifest manifest, GameInstallState state, string relative, bool allowMissing)
    {
        var path = state.ContentPrefix + relative.Replace('\\', '/');
        if (manifest.Files.TryGetValue(path, out var file))
        {
            return !file.IsLink ? file.Hash : throw new InvalidDataException($"Epic's {relative} is a link and cannot be verified as an original file.");
        }
        return allowMissing ? [] : throw new InvalidDataException($"Epic's reference manifest does not identify {relative}.");
    }

    [SuppressMessage("Security", "CA5350", Justification = "Epic BuildPatch manifests use SHA-1 checksums.")]
    private static Task<byte[]> ChecksumAsync(Stream stream, CancellationToken token) => SHA1.HashDataAsync(stream, token).AsTask();
}
