using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SnowPatcher.App.Views;

namespace SnowPatcher.App.Services.Desktop;

public sealed class DesktopSession : IDisposable
{
    private readonly MainWindow _window;
    private readonly PreferencesService _preferences;
    private readonly IDesktopIntegration _desktop;
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private bool _explicitShow;

    public DesktopSession(MainWindow window, PreferencesService preferences, IClassicDesktopStyleApplicationLifetime lifetime,
        IDesktopIntegration? desktop = null)
    {
        _window = window;
        _preferences = preferences;
        _lifetime = lifetime;
        _desktop = desktop ?? (OperatingSystem.IsWindows()
            ? new WindowsDesktopIntegration(Show, OpenPreferences, Exit)
            : new UnixDesktopIntegration(Show, OpenPreferences, Exit));
        _window.Session = this;
        _preferences.Changed += OnPreferencesChanged;
        _window.Closed += OnWindowClosed;
    }

    public bool BackgroundAvailable { get; private set; }

    public async Task StartAsync(string[] args)
    {
        ApplyBackgroundPreference();
        var forceShow = args.Contains("--show", StringComparer.Ordinal) || _explicitShow;
        var minimized = !forceShow && (args.Contains("--background", StringComparer.Ordinal) || _preferences.Current.StartMinimized);
        if (!minimized || !_preferences.Current.RunInBackground || !BackgroundAvailable)
        {
            if (minimized)
            {
                _window.WindowState = WindowState.Minimized;
            }

            _window.Show();
        }
        await _window.InitializeWindowAsync();
        if (args.Contains("--background", StringComparer.Ordinal) && !_window.Model.IsClosing)
        {
            try
            {
                if (_window.Model.ReadApplicationUpdateNotice() is { } notice)
                {
                    Notify(new UserNotice("self-update:" + notice.Version + notice.Success, notice.Success ? "SnowPatcher updated" : "SnowPatcher update needs attention", notice.Message, !notice.Success));
                }
            }
            catch (Exception noticeError) when (noticeError is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _window.Model.SetStatus(noticeError.Message);
            }
        }
        if (_preferences.LoadError is { } error)
        {
            _window.Model.SetStatus(error);
        }
    }

    public void Show()
    {
        _explicitShow = true;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _ = _window.PresentPendingDialogsAsync();
    }

    public void Notify(UserNotice notice)
    {
        var allowed = notice.Attention ? _preferences.Current.NotifyAttention : _preferences.Current.NotifyCompleted;
        if (!allowed || !_notified.Add(notice.Key))
        {
            return;
        }
        if (_window.IsActive && !_window.IsInBackground)
        {
            return;
        }
        try
        { _desktop.Notify(notice.Title, notice.Message); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _window.Model.SetStatus(notice.Message);
        }
    }

    public async void OpenPreferences()
    {
        Show();
        await _window.ShowPreferencesAsync();
    }

    public void Exit() => _window.ExitApplication();

    private void OnPreferencesChanged(object? sender, EventArgs args)
    {
        ApplyBackgroundPreference();
        if (!_preferences.Current.RunInBackground && !_window.IsVisible)
        {
            Show();
        }
    }

    private void ApplyBackgroundPreference()
    {
        try
        { BackgroundAvailable = _desktop.SetBackgroundEnabled(_preferences.Current.RunInBackground); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            BackgroundAvailable = false;
            _window.Model.SetStatus("Background mode is unavailable. " + error.Message);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        Dispose();
        _lifetime.Shutdown();
    }

    public void Dispose()
    {
        _preferences.Changed -= OnPreferencesChanged;
        _window.Closed -= OnWindowClosed;
        _window.Session = null;
        BackgroundAvailable = false;
        _desktop.Dispose();
    }
}
