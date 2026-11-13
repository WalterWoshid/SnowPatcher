using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Mods;

public sealed partial class ModLibraryStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly string _root;
    private FileStream? _lock;
    public bool BundledModsInitialized { get; private set; }

    public ModLibraryStore(string? root = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (root is null && string.IsNullOrEmpty(data))
        {
            throw new IOException("Your user data folder could not be located.");
        }

        _root = Path.GetFullPath(root ?? Path.Combine(data, "SnowPatcher", "Library"));
    }

    public IReadOnlyList<LibraryMod> Load()
    {
        EnsureDirectory(_root);
        try
        {
            _lock ??= new FileStream(Path.Combine(_root, "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("The mod library is open in another SnowPatcher window.");
        }

        var path = Path.Combine(_root, "library.json");
        Patching.PatchStorage.RejectLinks(path);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var input = File.OpenRead(path);
            var library = JsonSerializer.Deserialize<LibraryFile>(input, JsonOptions);
            if (library is null || library.FormatVersion != 1 || library.Mods is null || library.Mods.Any(m => m is null || m.Id == Guid.Empty || !Path.IsPathFullyQualified(m.SourcePath) || m.Sha256 is null || m.Sha256.Length != 64 || m.PreviousDownloads is null || (m.Source is { } s && (s.ModId <= 0 || s.FileId <= 0 || s.Md5?.Length != 32)) || (m.Latest is { } l && (m.Source is null || l.ModId != m.Source.ModId))) || library.Mods.Select(m => m.Id).Distinct().Count() != library.Mods.Length)
            {
                throw new InvalidDataException("The saved mod library is invalid. Its files have not been changed.");
            }

            BundledModsInitialized = library.BundledModsInitialized;
            return library.Mods;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The saved mod library could not be read. Its files have not been changed.");
        }
    }

    public void Save(IEnumerable<LibraryMod> mods, bool initializeBundledMods = false)
    {
        if (_lock is null)
        {
            throw new IOException("The mod library is not open.");
        }

        var path = Path.Combine(_root, "library.json");
        Patching.PatchStorage.RejectLinks(path);
        var temporary = Path.Combine(_root, $"library-{Guid.NewGuid():N}.tmp");
        try
        {
            var initialized = BundledModsInitialized || initializeBundledMods;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new LibraryFile(1, mods.ToArray(), initialized), JsonOptions);
                output.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
            BundledModsInitialized = initialized;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private static void EnsureDirectory(string path)
    {
        Patching.PatchStorage.RejectLinks(path);
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The mod library cannot write through a linked directory.");
        }
    }

    private sealed record LibraryFile(int FormatVersion, LibraryMod[] Mods, bool BundledModsInitialized = false);
}
