using System.Runtime.InteropServices;

namespace SnowPatcher.App.Services.Desktop;

internal sealed partial class WindowsDesktopIntegration
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    // These structures contain inline strings and use the Windows runtime marshaller.
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref IconData data);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass type);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindow(uint extendedStyle, string type, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefaultWindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPosition(out Point point);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
}
