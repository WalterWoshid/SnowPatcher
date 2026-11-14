using System.Diagnostics;
using System.Text.Json;
using SnowPatcher.Core.Patching;

namespace SnowPatcher.Core.Updates;

public sealed class ApplicationUpdateService
{
    private readonly GitHubUpdateClient _client;
    private readonly string _installation;
    private readonly string _updates;

    public ApplicationUpdateService(GitHubUpdateClient? client = null, string? installationDirectory = null, string? updateDirectory = null)
    {
        _client = client ?? new GitHubUpdateClient();
        _installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory ?? AppContext.BaseDirectory));
        _updates = Path.TrimEndingDirectorySeparator(Path.GetFullPath(updateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowPatcher", "Updates")));
        PendingMods = new PendingModInstallStore(_updates, _installation);
    }

    public PendingModInstallStore PendingMods { get; }
    public Task<ApplicationRelease?> CheckAsync(CancellationToken token) => _client.CheckAsync(token);

    public async Task<PreparedApplicationUpdate> PrepareAsync(ApplicationRelease release, IProgress<int>? progress, CancellationToken token, bool restartInBackground = false)
    {
        if (!File.Exists(UpdateFiles.In(_installation, UpdateManifest.FileName)))
        {
            throw new IOException("Automatic updates need a complete SnowPatcher release. Run the latest setup or extract the portable ZIP first.");
        }

        var installed = UpdateFiles.Manifest(_installation);
        if (installed.Platform != release.Platform || release.Platform != GitHubUpdateClient.Platform ||
            SnowPatcherVersion.Compare(release.Version, installed.Version) <= 0)
        {
            throw new IOException("The update does not match this SnowPatcher installation.");
        }

        PatchStorage.RejectLinks(_updates);
        var directory = Path.Combine(_updates, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "download");
        var staged = Path.Combine(directory, "staged");
        Directory.CreateDirectory(staged);
        try
        {
            await _client.DownloadAsync(release, archive, progress, token).ConfigureAwait(false);
            await Task.Run(() => UpdateArchive.Extract(archive, staged, release.Platform, token), token).ConfigureAwait(false);
            var manifest = UpdateFiles.Manifest(staged);
            if (manifest.Version != release.Version || manifest.Platform != release.Platform)
            {
                throw new IOException("The downloaded application has different update metadata.");
            }

            await Task.Run(() => UpdateFiles.Verify(staged, manifest, token, exact: true), token).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var plan = new UpdatePlan(_installation, staged, release.Version, UpdateFiles.Hash(Path.Combine(staged, UpdateManifest.FileName)),
                process.Id, process.StartTime.ToUniversalTime().Ticks, RestartInBackground: restartInBackground);
            var planPath = Path.Combine(directory, "plan.json");
            UpdateFiles.Write(planPath, plan);
            File.Delete(archive);
            return new PreparedApplicationUpdate(UpdateFiles.In(staged, ExecutableName), planPath);
        }
        catch
        {
            RemoveStaging(directory);
            throw;
        }
    }

    public static async Task StartHelperAsync(PreparedApplicationUpdate update, CancellationToken token = default)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(update.PlanPath))!;
        var start = new ProcessStartInfo(update.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(update.ExecutablePath)!
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(update.PlanPath);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the SnowPatcher updater.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                if (File.Exists(Path.Combine(root, "ready.json")) && UpdateFiles.Read<UpdateReady>(Path.Combine(root, "ready.json")).ProcessId == process.Id)
                {
                    return;
                }

                if (process.HasExited)
                {
                    var resultPath = Path.Combine(root, "result.json");
                    throw new IOException(File.Exists(resultPath) ? UpdateFiles.Read<UpdateResult>(resultPath).Message : "The SnowPatcher updater could not start.");
                }

                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            UpdateFiles.Write(Path.Combine(root, "cancelled.json"), true);
            throw;
        }
    }

    public string? ReadLastResult() => ReadLastNotice()?.Message;

    public void DiscardPrepared(PreparedApplicationUpdate update)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(update.PlanPath))!;
        if (File.Exists(Path.Combine(directory, "ready.json")))
        {
            throw new IOException("The update helper has already started.");
        }
        RemoveStaging(directory);
    }

    public ApplicationUpdateNotice? ReadLastNotice()
    {
        if (!Directory.Exists(_updates))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(_updates).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var resultPath = Path.Combine(directory, "result.json");
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !File.Exists(resultPath))
                {
                    continue;
                }

                PatchStorage.RejectLinks(resultPath);
                var plan = UpdateFiles.Read<UpdatePlan>(UpdateFiles.In(directory, "plan.json"));
                if (!UpdateFiles.SameDirectory(plan.InstallationDirectory, _installation))
                {
                    continue;
                }

                var result = UpdateFiles.Read<UpdateResult>(resultPath);
                if (result.Version is null || !SnowPatcherVersion.IsValid(result.Version) || string.IsNullOrWhiteSpace(result.Message))
                {
                    continue;
                }
                if (result.Success && result.Version == SnowPatcherVersion.Current && !IsRunning(result.HelperProcessId))
                {
                    RemoveStaging(directory);
                }

                return new ApplicationUpdateNotice(result.Version, result.Success, result.Message);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                // A running helper may still hold its staging files open.
            }
        }

        return null;
    }

    private void RemoveStaging(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(Path.GetDirectoryName(path), _updates, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(path), "N", out _))
        {
            throw new IOException("Refusing to remove a folder outside SnowPatcher's update cache.");
        }

        PatchStorage.RejectLinks(path);
        try
        { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static string ExecutableName => OperatingSystem.IsWindows() ? "SnowPatcher.exe" : "SnowPatcher";

    private static bool IsRunning(int id)
    {
        try
        { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
