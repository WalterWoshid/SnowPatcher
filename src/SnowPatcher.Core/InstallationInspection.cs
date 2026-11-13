namespace SnowPatcher.Core;

public enum FileVerificationState
{
    Original,
    Changed,
    UnableToVerify
}

public sealed record InstallationInspection(string? BuildId, FileVerificationState State, string Detail,
    DateTimeOffset? ReleasedAt = null);
