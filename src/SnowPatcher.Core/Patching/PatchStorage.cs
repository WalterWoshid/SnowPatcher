using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Patching;

internal sealed record ManagedFile(string Path, string BaselineHash, string OutputHash);
internal sealed record ManagedMod(string Id, string Version, string ArchiveHash, string[] Operations);
internal sealed record ManagedState(int Format, string TransactionId, string InstallationPath, string BuildId, string DepotFingerprint,
    ManagedFile[] Files, ManagedMod[] Mods);
internal sealed record JournalFile(string Path, string BeforeHash, string AfterHash);
internal sealed record PatchJournal(int Format, string Id, string InstallationPath, string BuildId, string DepotFingerprint, JournalFile[] Files);

internal sealed class PatchStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public string DirectoryPath
    { get; }
    public string StatePath => Path.Combine(DirectoryPath, "state.json");
    public string JournalPath => Path.Combine(DirectoryPath, "pending.json");
    public string InstallationPath
    { get; }

    public PatchStorage(string root, GameInstallation installation)
    {
        InstallationPath = Normalize(installation.RootDirectory);
        DirectoryPath = Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(InstallationPath))));
        RejectLinks(DirectoryPath);
    }

    public ManagedState? ReadState()
    {
        var state = ReadJson<ManagedState>(StatePath);
        if (state is null)
        {
            return null;
        }

        ValidateState(state);

        return state;
    }

    private void ValidateState(ManagedState state)
    {
        if (state.Format != 1 || state.InstallationPath != InstallationPath || !ValidId(state.TransactionId) ||
            state.Files is null || state.Mods is null || state.Files.Any(f => f is null || string.IsNullOrEmpty(f.Path)) ||
            state.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Length)
        {
            throw new InvalidDataException("SnowPatcher's saved installation state is invalid. Your game files were kept.");
        }

        ValidateIdentity(state.BuildId, state.DepotFingerprint);
        foreach (var file in state.Files)
        {
            PackagePath.Validate(file.Path);
            ValidateHash(file.BaselineHash);
            ValidateHash(file.OutputHash);
        }
        foreach (var mod in state.Mods)
        {
            if (mod is null || string.IsNullOrEmpty(mod.Id) || string.IsNullOrEmpty(mod.Version) || mod.Operations is null || !HashValue(mod.ArchiveHash))
            {
                throw new InvalidDataException("SnowPatcher's saved package state is invalid.");
            }
        }
    }

    public ManagedState? FindMatchingHistory(Steam.SteamInstallState steam,
        IReadOnlyDictionary<string, byte[]?> current, CancellationToken cancellationToken)
    {
        var history = Path.Combine(DirectoryPath, "history");
        RejectLinks(history);
        if (!Directory.Exists(history))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(history, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var state = ReadJson<ManagedState>(path);
                if (state is null)
                {
                    continue;
                }

                ValidateState(state);
                if (state.BuildId != steam.BuildId || state.DepotFingerprint != steam.DepotFingerprint ||
                    state.Files.Length != current.Count)
                {
                    continue;
                }

                if (state.Files.All(file => current.TryGetValue(file.Path, out var bytes) && Hash(bytes) == file.OutputHash))
                {
                    return state;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public PatchJournal? ReadJournal()
    {
        var journal = ReadJson<PatchJournal>(JournalPath);
        if (journal is null)
        {
            return null;
        }

        if (journal.Format != 1 || !ValidId(journal.Id) || journal.InstallationPath != InstallationPath || journal.Files is null || journal.Files.Any(f => f is null || string.IsNullOrEmpty(f.Path)) ||
            journal.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
        {
            throw new InvalidDataException("SnowPatcher's recovery record is invalid. Keep its backups and game files.");
        }

        ValidateIdentity(journal.BuildId, journal.DepotFingerprint);
        foreach (var file in journal.Files)
        {
            PackagePath.Validate(file.Path);
            ValidateHash(file.BeforeHash);
            ValidateHash(file.AfterHash);
        }
        return journal;
    }

    public byte[]? Blob(string hash)
    {
        ValidateHash(hash);
        if (hash == "absent")
        {
            return null;
        }

        var bytes = ReadFile(Path.Combine(DirectoryPath, "objects", hash));
        if (Hash(bytes) != hash)
        {
            throw new InvalidDataException("A saved starting file or recovery copy failed its checksum. No game files were changed.");
        }

        return bytes;
    }

    public void SaveBlob(byte[]? bytes)
    {
        if (bytes is null)
        {
            return;
        }

        var hash = Hash(bytes);
        var path = Path.Combine(DirectoryPath, "objects", hash);
        if (File.Exists(path))
        {
            _ = Blob(hash);
            return;
        }
        SaveNew(path, bytes);
        _ = Blob(hash);
    }

    public void SaveState(ManagedState state) => WriteJson(StatePath, state);
    public void SaveJournal(PatchJournal journal) => WriteJson(JournalPath, journal);
    public void SaveHistory(string id, ManagedState state) => WriteJson(Path.Combine(DirectoryPath, "history", id + ".json"), state);

    public static string GamePath(GameInstallation installation, string relative)
    {
        PackagePath.Validate(relative);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.RootDirectory));
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("A patch target is outside the game folder.");
        }

        RejectLinks(path);
        return path;
    }

    public static byte[]? ReadFile(string path)
    {
        RejectLinks(path);
        if (System.IO.Directory.Exists(path))
        {
            throw new IOException("A file target is a folder.");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > Compatibility.GameResources.MaxBytes)
        {
            throw new InvalidDataException("A patch file exceeds the supported 256 MiB limit.");
        }

        return Compatibility.GameResources.ReadBounded(input, input.Length, CancellationToken.None);
    }

    public static string Hash(byte[]? bytes) => bytes is null ? "absent" : Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Patching cannot follow linked files or folders.");
            }
        }
    }

    public static void SaveNew(string path, byte[] bytes)
    {
        RejectLinks(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
    }

    public static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        RejectLinks(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var input = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(input, JsonOptions) ?? throw new InvalidDataException("SnowPatcher's saved state is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("SnowPatcher's saved state could not be read. Keep its recovery copies.", error); }
    }

    private static void WriteJson<T>(string path, T record)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        SaveNew(temp, bytes);
        try
        {
            RejectLinks(path);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool ValidId(string id) => Guid.TryParseExact(id, "N", out _);
    private static bool HashValue(string hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateHash(string hash)
    {
        if (hash != "absent" && !HashValue(hash))
        {
            throw new InvalidDataException("Invalid saved file checksum.");
        }
    }
    private static void ValidateIdentity(string build, string fingerprint)
    {
        if (!ulong.TryParse(build, out var number) || number == 0 || fingerprint is not { Length: 64 } || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Invalid saved Steam build identity.");
        }
    }
}
