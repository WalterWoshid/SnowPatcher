namespace SnowPatcher.Core.Updates;

public sealed record ApplicationUpdateNotice(string Version, bool Success, string Message);
