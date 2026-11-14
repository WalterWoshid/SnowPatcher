using SnowPatcher.Core.Settings;

namespace SnowPatcher.App.ViewModels;

public sealed class PreferencesViewModel : ObservableObject
{
    private AppPreferences _draft;
    public PreferencesViewModel(AppPreferences preferences, bool supportsStartup = true)
    {
        _draft = preferences;
        SupportsStartup = supportsStartup;
    }

    public bool SupportsStartup { get; }
    public string StartupLabel { get; } = OperatingSystem.IsWindows() ? "Start with Windows" : "Start when I sign in";
    public bool StartWithSystem { get => _draft.StartWithSystem; set => Change(_draft with { StartWithSystem = value }); }
    public bool StartMinimized { get => _draft.StartMinimized; set => Change(_draft with { StartMinimized = value }); }
    public bool RunInBackground { get => _draft.RunInBackground; set => Change(_draft with { RunInBackground = value }); }
    public bool NotifyAttention { get => _draft.NotifyAttention; set => Change(_draft with { NotifyAttention = value }); }
    public bool NotifyCompleted { get => _draft.NotifyCompleted; set => Change(_draft with { NotifyCompleted = value }); }
    public bool CheckApplicationUpdates { get => _draft.CheckApplicationUpdates; set => Change((_draft with { CheckApplicationUpdates = value }).Normalize()); }
    public bool AutomaticallyUpdateApplication { get => _draft.AutomaticallyUpdateApplication; set => Change(_draft with { AutomaticallyUpdateApplication = value }); }
    public bool CheckModUpdates { get => _draft.CheckModUpdates; set => Change((_draft with { CheckModUpdates = value }).Normalize()); }
    public bool AutomaticallyUpdateMods { get => _draft.AutomaticallyUpdateMods; set => Change(_draft with { AutomaticallyUpdateMods = value }); }
    public int GameUpdateAction
    {
        get => (int)_draft.GameUpdates;
        set
        {
            if (Enum.IsDefined((GameUpdateBehavior)value))
            {
                Change(_draft with { GameUpdates = (GameUpdateBehavior)value });
            }
        }
    }
    public AppPreferences Result => _draft.Normalize();

    public void Reset() => Change(new AppPreferences { GameFolder = _draft.GameFolder });

    private void Change(AppPreferences value)
    {
        if (_draft == value)
        {
            return;
        }

        _draft = value;
        foreach (var name in new[] { nameof(StartWithSystem), nameof(StartMinimized), nameof(RunInBackground), nameof(NotifyAttention), nameof(NotifyCompleted),
            nameof(CheckApplicationUpdates), nameof(AutomaticallyUpdateApplication), nameof(CheckModUpdates), nameof(AutomaticallyUpdateMods), nameof(GameUpdateAction) })
        {
            RaisePropertyChanged(name);
        }
    }
}
