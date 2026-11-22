using System.Diagnostics;
using System.IO.Compression;
using SnowPatcher.App.ViewModels;
using SnowPatcher.Core;
using SnowPatcher.Core.Backups;
using SnowPatcher.Core.Compatibility;
using SnowPatcher.Core.Mods;
using SnowPatcher.Core.Patching;
using SnowPatcher.Core.Updates;

internal static class AutomaticSelectionChecks
{
    public static async Task RunAsync(string root, PatchInstaller installer, GameInstallation game, GameInstallation other,
        CompatibilityInput first, CompatibilityInput second, CompatibilityInput conflicting, string languageFile)
    {
        var libraryPath = Path.Combine(root, "automatic-library");
        using (var library = new ModLibraryStore(libraryPath))
        {
            library.Load();
            var archive = await library.ImportAsync(first.Archive.SourcePath);
            library.Save([new LibraryMod(first.LibraryId, archive.SourcePath, archive.Sha256, true,
                null, null, null, [], first.Archive.Package!.Manifest.Id)]);
        }

        var original = File.ReadAllBytes(game.InitialPakPath);
        await using (var window = CreateWindow())
        {
            await window.InitializeLibraryAsync();
            await window.SelectGameFolderAsync(game.RootDirectory);
            await window.CheckCompatibilityAsync();
            await window.ApplyQueuedSelectionAsync();
            Require(File.ReadAllBytes(game.InitialPakPath).SequenceEqual(original), "An older library's selection was applied without a new user action.");
        }

        using var runningGame = StartGameProcess(game);
        try
        {
            await using var window = CreateWindow();
            await window.InitializeLibraryAsync();
            await window.SelectGameFolderAsync(game.RootDirectory);
            await window.AddArchivesAsync([second.Archive.SourcePath]);
            await window.CheckCompatibilityAsync();
            await window.ApplyQueuedSelectionAsync();
            Require(window.IsGameRunning && window.StatusText == "Pending - applies when SnowRunner closes.", "The running game must show a pending selection.");
            Require(File.ReadAllBytes(game.InitialPakPath).SequenceEqual(original), "A queued import patched a running game.");
        }
        finally
        {
            if (!runningGame.HasExited)
            {
                runningGame.Kill();
            }
            await runningGame.WaitForExitAsync();
        }

        await using (var window = CreateWindow())
        {
            await window.InitializeLibraryAsync();
            await window.SelectGameFolderAsync(other.RootDirectory);
            await window.CheckCompatibilityAsync();
            await window.ApplyQueuedSelectionAsync();
            Require(!Contains(other, "EXTRA_LABEL"), "The queued selection was applied to another installation.");

            await window.SelectGameFolderAsync(game.RootDirectory);
            await window.CheckCompatibilityAsync();
            await window.ApplyQueuedSelectionAsync();
            Require(Contains(game, "MOD_CAPTION") && Contains(game, "EXTRA_LABEL") && window.StatusText == "Mods applied.",
                "The closed game did not receive the queued selection after SnowPatcher restarted.");

            window.AutomaticActionsPaused = true;
            window.Mods.Single(m => m.ToRecord().PackageId == first.Archive.Package!.Manifest.Id).IsEnabled = false;
            await window.CheckCompatibilityAsync();
            await window.ApplyQueuedSelectionAsync();
            Require(Contains(game, "MOD_CAPTION") && window.StatusText == "Pending - automatic actions are paused.",
                "Paused automatic actions must defer a toggle.");
            window.AutomaticActionsPaused = false;
            await window.ApplyQueuedSelectionAsync();
            Require(!Contains(game, "MOD_CAPTION") && Contains(game, "EXTRA_LABEL"), "Disabling a mod did not recompose the active selection.");

            var beforeConflict = File.ReadAllBytes(game.InitialPakPath);
            window.Mods.Single(m => m.ToRecord().PackageId == first.Archive.Package!.Manifest.Id).IsEnabled = true;
            await window.AddArchivesAsync([conflicting.Archive.SourcePath]);
            await window.CheckCompatibilityAsync();
            await window.ApplyQueuedSelectionAsync();
            Require(window.Compatibility?.HasBlockingIssues == true && window.StatusText.Contains("overlap", StringComparison.OrdinalIgnoreCase),
                "A conflicting selection must explain why it could not apply.");
            Require(File.ReadAllBytes(game.InitialPakPath).SequenceEqual(beforeConflict), "A conflict changed the live archive.");
        }

        Console.WriteLine("Automatic selection checks passed.");

        MainWindowViewModel CreateWindow() => new(
            modio: new ModioClient(null),
            library: new ModLibraryStore(libraryPath),
            patchInstaller: installer,
            backupStore: new BackupStore(Path.Combine(root, "automatic-backups")),
            includeBundledMods: false,
            applicationUpdates: new ApplicationUpdateService(updateDirectory: Path.Combine(root, "automatic-updates")));

        bool Contains(GameInstallation installation, string key)
        {
            using var zip = ZipFile.OpenRead(installation.InitialPakPath);
            using var reader = new StreamReader(zip.GetEntry(languageFile)!.Open());
            return reader.ReadToEnd().Contains(key, StringComparison.Ordinal);
        }
    }

    private static Process StartGameProcess(GameInstallation game)
    {
        // A harmless executable in the fixture exercises the real process guard.
        var windows = OperatingSystem.IsWindows();
        var path = Path.Combine(game.RootDirectory, windows ? "SnowRunner.exe" : "SnowRunner");
        File.Copy(windows ? Path.Combine(Environment.SystemDirectory, "ping.exe") : "/bin/sleep", path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        return Process.Start(new ProcessStartInfo(path, windows ? "-t 127.0.0.1" : "90")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("The fixture game process could not start.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
