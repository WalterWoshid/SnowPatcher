using SnowPatcher.Core.Updates;

namespace SnowPatcher.App.Services;

public sealed record UserNotice(string Key, string Title, string Message, bool Attention);
public sealed record MaintenanceResult(IReadOnlyList<UserNotice> Notices, bool ShowApplication = false, PreparedApplicationUpdate? ApplicationUpdate = null);
public sealed record AutomaticModResult(int Updated, string? Error = null);
