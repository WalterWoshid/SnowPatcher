using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

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
        var glyph = new Border
        {
            Width = 32,
            Height = 32,
            Background = Brushes.DimGray,
            Child = new TextBlock
            {
                Text = "S",
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }
        };
        glyph.Measure(new Size(32, 32));
        glyph.Arrange(new Rect(0, 0, 32, 32));
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32));
        bitmap.Render(glyph);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    public void Notify(string title, string message)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--app-name=SnowPatcher", "--icon=applications-system", "--hint=string:suppress-sound:true", "--", title, message })
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
