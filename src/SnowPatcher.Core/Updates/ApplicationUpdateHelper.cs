using System.Diagnostics;
using System.Text.Json;

namespace SnowPatcher.Core.Updates;

public static class ApplicationUpdateHelper
{
    public static async Task<int> RunAsync(string planPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        UpdatePlan? plan = null;
        var restored = true;
        var parentExited = false;
        try
        {
            plan = UpdateFiles.Read<UpdatePlan>(planPath);
            if (!UpdateFiles.SameDirectory(plan.StagedDirectory, AppContext.BaseDirectory) ||
                !UpdateFiles.SameDirectory(Path.GetDirectoryName(Path.GetFullPath(plan.StagedDirectory))!, root) ||
                UpdateFiles.SameDirectory(plan.InstallationDirectory, plan.StagedDirectory))
            {
                throw new IOException("The update helper must run from its own staging folder.");
            }

            if (UpdateFiles.Hash(UpdateFiles.In(plan.StagedDirectory, UpdateManifest.FileName)) != plan.ManifestHash)
            {
                throw new IOException("The staged update metadata changed.");
            }

            var manifest = UpdateFiles.Manifest(plan.StagedDirectory);
            if (manifest.Version != plan.Version || manifest.Platform != GitHubUpdateClient.Platform)
            {
                throw new IOException("The staged update does not match this application.");
            }

            UpdateFiles.Verify(plan.StagedDirectory, manifest, CancellationToken.None, exact: true);
            UpdateFiles.Write(Path.Combine(root, "ready.json"), new UpdateReady(Environment.ProcessId));
            await WaitForParentAsync(plan, root).ConfigureAwait(false);
            parentExited = true;
            using var updateLock = new FileStream(UpdateFiles.In(plan.InstallationDirectory, ".snowpatcher-update.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            if (File.Exists(Path.Combine(root, "result.json")))
            {
                return UpdateFiles.Read<UpdateResult>(Path.Combine(root, "result.json")).Success ? 0 : 1;
            }

            if (File.Exists(Path.Combine(root, "journal.json")))
            {
                UpdateTransaction.Rollback(root, plan.InstallationDirectory);
                UpdateFiles.Write(Path.Combine(root, "result.json"), new UpdateResult(false, true, plan.Version,
                    "An interrupted SnowPatcher update was rolled back. Your previous version was restored."));
                Restart(plan);
                return 1;
            }

            UpdateTransaction.Install(root, plan, manifest);
            UpdateFiles.Write(Path.Combine(root, "result.json"), new UpdateResult(true, false, plan.Version, $"Updated SnowPatcher to {plan.Version}."));
            Restart(plan);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var message = "SnowPatcher could not update: " + error.Message;
            if (plan is not null && File.Exists(Path.Combine(root, "journal.json")))
            {
                try
                { UpdateTransaction.Rollback(root, plan.InstallationDirectory); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    restored = false;
                    message += " Recovery could not finish. The previous files are kept in " + Path.Combine(root, "backup") + ". " + rollbackError.Message;
                }
            }

            if (restored)
            {
                message += " Your previous version was kept.";
            }

            UpdateFiles.Write(Path.Combine(root, "result.json"), new UpdateResult(false, restored, plan?.Version ?? "", message));
            if (plan is not null && restored && parentExited)
            {
                try
                { Restart(plan); }
                catch (Exception restartError) when (restartError is IOException or System.ComponentModel.Win32Exception) { }
            }

            return 1;
        }
    }

    private static async Task WaitForParentAsync(UpdatePlan plan, string root)
    {
        ThrowIfCancelled(root);
        Process parent;
        try
        { parent = Process.GetProcessById(plan.ParentProcessId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTimeUtcTicks)
            {
                throw new IOException("The original SnowPatcher process could not be identified.");
            }

            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!parent.HasExited)
            {
                ThrowIfCancelled(root);
                if (DateTime.UtcNow >= deadline)
                {
                    throw new IOException("SnowPatcher did not finish closing. The update was not installed.");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            ThrowIfCancelled(root);
        }
    }

    private static void ThrowIfCancelled(string root)
    {
        if (File.Exists(Path.Combine(root, "cancelled.json")))
        {
            throw new IOException("The update was cancelled before SnowPatcher closed.");
        }
    }

    private static void Restart(UpdatePlan plan)
    {
        if (!plan.Restart)
        {
            return;
        }

        var executable = UpdateFiles.In(plan.InstallationDirectory, ApplicationUpdateService.ExecutableName);
        if (!File.Exists(executable))
        {
            executable = UpdateFiles.In(plan.InstallationDirectory, OperatingSystem.IsWindows() ? "SnowPatcher.App.exe" : "SnowPatcher.App");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = plan.InstallationDirectory
        };
        start.ArgumentList.Add(plan.RestartInBackground ? "--background" : "--show");
        using var process = Process.Start(start) ?? throw new IOException("Could not restart SnowPatcher.");
    }
}
