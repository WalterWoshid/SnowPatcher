using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SnowPatcher.App.Services.Desktop;

internal sealed class UnixDesktopIntegration(Action open, Action preferences, Action exit) : IDesktopIntegration
{
    private TrayIcon? _tray;

    public bool SetBackgroundEnabled(bool enabled)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        if (!enabled)
        {
            Dispose();
            return true;
        }
        if (_tray is not null)
        {
            return true;
        }

        var menu = new NativeMenu();
        Add("Open SnowPatcher", open);
        Add("Preferences...", preferences);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add("Exit", exit);
        _tray = new TrayIcon { Icon = CreateIcon(), ToolTipText = "SnowPatcher", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => open();
        TrayIcon.SetIcons(Application.Current!, [_tray]);
        return true;

        void Add(string text, Action action)
        {
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private static WindowIcon CreateIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://SnowPatcher/Assets/SnowPatcher.ico"));
        return new WindowIcon(stream);
    }

    public void Notify(string title, string message)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--app-name=SnowPatcher", "--icon=" + Path.Combine(AppContext.BaseDirectory, "Assets", "SnowPatcher.png"), "--hint=string:suppress-sound:true", "--", title, message })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start);
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
    }
}
