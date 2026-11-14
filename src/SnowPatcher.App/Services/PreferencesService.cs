using SnowPatcher.Core.Settings;

namespace SnowPatcher.App.Services;

public sealed class PreferencesService
{
    private readonly PreferencesStore _store;
    private readonly IStartupRegistration _startup;

    public PreferencesService(PreferencesStore? store = null, IStartupRegistration? startup = null)
    {
        _store = store ?? new PreferencesStore();
        _startup = startup ?? new StartupRegistration();
        try
        { Current = _store.Load(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Current = new AppPreferences();
            LoadError = error.Message + " Default preferences are in use.";
        }

        try
        {
            if (_startup.IsEnabled is { } startupEnabled)
            {
                Current = Current with { StartWithSystem = startupEnabled };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { LoadError = "Startup registration could not be read. " + error.Message; }
    }

    public AppPreferences Current { get; private set; }
    public string? LoadError { get; }
    public bool SupportsStartup => _startup.IsSupported;
    public event EventHandler? Changed;

    public void Save(AppPreferences preferences)
    {
        preferences = preferences.Normalize();
        var startupChanged = preferences.StartWithSystem != Current.StartWithSystem;
        if (startupChanged)
        {
            _startup.SetEnabled(preferences.StartWithSystem);
        }
        try
        { _store.Save(preferences); }
        catch
        {
            if (startupChanged)
            {
                _startup.SetEnabled(Current.StartWithSystem);
            }
            throw;
        }

        Current = preferences;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RememberGameFolder(string folder)
    {
        if (string.Equals(Current.GameFolder, folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        var next = Current with { GameFolder = folder };
        _store.Save(next);
        Current = next;
    }
}
