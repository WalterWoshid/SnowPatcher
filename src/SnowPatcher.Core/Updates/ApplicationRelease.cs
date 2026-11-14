namespace SnowPatcher.Core.Updates;

public sealed record ApplicationRelease(string Version, string Platform, Uri DownloadUri, long Size, string Sha256);

public sealed record PreparedApplicationUpdate(string ExecutablePath, string PlanPath);

internal sealed record UpdateManifest(int FormatVersion, string Version, string Platform, UpdateFile[] Files)
{
    internal const string FileName = "snowpatcher-update.json";
}

internal sealed record UpdateFile(string Path, long Size, string Sha256);
internal sealed record UpdatePlan(string InstallationDirectory, string StagedDirectory, string Version, string ManifestHash,
    int ParentProcessId, long ParentStartTimeUtcTicks, bool Restart = true, bool RestartInBackground = false);
internal sealed record UpdatePreimage(string Path, string? Sha256);
internal sealed record UpdateJournal(UpdatePreimage[] Files);
internal sealed record UpdateReady(int ProcessId);
internal sealed record UpdateResult(bool Success, bool Restored, string Version, string Message)
{
    public int HelperProcessId { get; init; } = Environment.ProcessId;
}
