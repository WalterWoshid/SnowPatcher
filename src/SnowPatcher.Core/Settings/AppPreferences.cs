namespace SnowPatcher.Core.Settings;

public enum GameUpdateBehavior { Notify, ShowApplication, ReapplyMods }

public sealed record AppPreferences
{
    public int FormatVersion { get; init; } = 1;
    public bool CheckApplicationUpdates { get; init; } = true;
    public bool AutomaticallyUpdateApplication { get; init; }
    public bool CheckModUpdates { get; init; } = true;
    public bool AutomaticallyUpdateMods { get; init; }
    public bool StartWithSystem { get; init; }
    public bool StartMinimized { get; init; }
    public bool RunInBackground { get; init; } = true;
    public GameUpdateBehavior GameUpdates { get; init; }
    public bool NotifyAttention { get; init; } = true;
    public bool NotifyCompleted { get; init; } = true;
    public string? GameFolder { get; init; }

    public AppPreferences Normalize() => this with
    {
        AutomaticallyUpdateApplication = CheckApplicationUpdates && AutomaticallyUpdateApplication,
        AutomaticallyUpdateMods = CheckModUpdates && AutomaticallyUpdateMods
    };
}
