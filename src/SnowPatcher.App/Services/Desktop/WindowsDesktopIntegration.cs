using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace SnowPatcher.App.Services.Desktop;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDesktopIntegration : IDesktopIntegration
{
    private readonly Action _open;
    private readonly Action _preferences;
    private readonly Action _exit;
    private readonly WindowProcedure _procedure;
    private readonly string _className = "SnowPatcher.Tray." + Guid.NewGuid().ToString("N");
    private readonly DispatcherTimer _temporaryIcon = new() { Interval = TimeSpan.FromSeconds(15) };
    private nint _window;
    private bool _registered;
    private bool _background;
    private uint _taskbarCreated;
    private const uint CallbackMessage = 0x8001;

    public WindowsDesktopIntegration(Action open, Action preferences, Action exit)
    {
        _open = open;
        _preferences = preferences;
        _exit = exit;
        _procedure = ProcessMessage;
        _temporaryIcon.Tick += (_, _) =>
        {
            _temporaryIcon.Stop();
            if (!_background)
            {
                RemoveIcon();
            }
        };
    }

    public bool SetBackgroundEnabled(bool enabled)
    {
        _background = enabled;
        if (enabled)
        {
            EnsureIcon();
        }
        else
        {
            _temporaryIcon.Stop();
            RemoveIcon();
        }
        return true;
    }

    public void Notify(string title, string message)
    {
        EnsureIcon();
        var data = Data();
        data.Flags = 0x10;
        data.InfoTitle = Fit(title, 63);
        data.Info = Fit(message, 255);
        data.InfoFlags = 0x1 | 0x10 | 0x80; // Information, no sound, respect quiet time.
        _ = ShellNotifyIcon(1, ref data);
        if (!_background)
        {
            _temporaryIcon.Stop();
            _temporaryIcon.Start();
        }
    }

    private void EnsureIcon()
    {
        if (_window == 0)
        {
            var type = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Instance = GetModuleHandle(null),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                ClassName = _className
            };
            if (RegisterClass(ref type) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            _window = CreateWindow(0, _className, "SnowPatcher", 0, 0, 0, 0, 0, 0, 0, type.Instance, 0);
            if (_window == 0)
            {
                _ = UnregisterClass(_className, type.Instance);
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }

        if (!_registered)
        {
            var data = Data();
            data.Flags = 1 | 2 | 4;
            if (!ShellNotifyIcon(0, ref data))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The system tray is unavailable.");
            }
            _registered = true;
            data.Version = 4;
            _ = ShellNotifyIcon(4, ref data);
        }
    }

    private IconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(),
        Window = _window,
        Id = 1,
        Callback = CallbackMessage,
        Icon = LoadIcon(0, 32512),
        Tip = "SnowPatcher",
        Info = "",
        InfoTitle = ""
    };

    private nint ProcessMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (_taskbarCreated != 0 && message == _taskbarCreated)
        {
            _registered = false;
            if (_background)
            {
                try
                { EnsureIcon(); }
                catch (Win32Exception) { }
            }
        }
        if (message == CallbackMessage)
        {
            var action = unchecked((uint)(long)lParam) & 0xffff;
            if (action is 0x400 or 0x401 or 0x405)
            {
                Dispatcher.UIThread.Post(_open);
            }
            else if (action is 0x7b or 0x205)
            {
                ShowMenu();
            }
            return 0;
        }
        return DefaultWindowProcedure(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }
        try
        {
            _ = AppendMenu(menu, 0, 1, "Open SnowPatcher");
            _ = AppendMenu(menu, 0, 2, "Preferences...");
            _ = AppendMenu(menu, 0x800, 0, null);
            _ = AppendMenu(menu, 0, 3, "Exit");
            _ = GetCursorPosition(out var point);
            _ = SetForegroundWindow(_window);
            var chosen = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _window, 0);
            _ = PostMessage(_window, 0, 0, 0);
            Action? action = chosen switch { 1 => _open, 2 => _preferences, 3 => _exit, _ => null };
            if (action is not null)
            {
                Dispatcher.UIThread.Post(action);
            }
        }
        finally { _ = DestroyMenu(menu); }
    }

    private void RemoveIcon()
    {
        if (_registered)
        {
            var data = Data();
            _ = ShellNotifyIcon(2, ref data);
            _registered = false;
        }
    }

    public void Dispose()
    {
        _temporaryIcon.Stop();
        RemoveIcon();
        if (_window != 0)
        {
            _ = DestroyWindow(_window);
            _ = UnregisterClass(_className, GetModuleHandle(null));
            _window = 0;
        }
    }

    private static string Fit(string value, int length) => value.Length <= length ? value : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];
}
