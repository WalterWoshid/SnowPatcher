namespace SnowPatcher.Core.Backups;

public sealed record ArchiveBackup(
    int SchemaVersion,
    string Id,
    string InstallationPath,
    string BuildId,
    string DepotFingerprint,
    DateTimeOffset CreatedAt,
    string Sha256,
    long Length,
    string Reason,
    string? Label = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DefaultName => Reason == "Automatic backup" ? "Initial backup" : Reason;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => Label ?? DefaultName;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Details => $"{CreatedAt.ToLocalTime():g}" + (Label is null ? "" : $" - {DefaultName}");
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => $"{CreatedAt.ToLocalTime():g} - {Name}";
}

public sealed record BackupRestoreResult(bool Changed);
