using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SnowPatcher.App.Services;

public interface IStartupRegistration
{
    bool IsSupported { get; }
    bool? IsEnabled => null;
    void SetEnabled(bool enabled);
}

public sealed class StartupRegistration : IStartupRegistration
{
    private readonly string _executable;
    private readonly string _linuxEntry;

    public StartupRegistration(string? executable = null, string? linuxConfigDirectory = null)
    {
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SnowPatcher.exe" : "SnowPatcher");
        _linuxEntry = Path.Combine(linuxConfigDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", "snowpatcher.desktop");
    }

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public bool? IsEnabled
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return string.Equals(key?.GetValue("SnowPatcher") as string, "\"" + _executable + "\" --startup", StringComparison.OrdinalIgnoreCase);
            }

            return OperatingSystem.IsLinux() ? OwnsLinuxEntry() : null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && !File.Exists(_executable))
        {
            throw new IOException("The SnowPatcher executable could not be found. Reinstall or extract the application first.");
        }

        if (OperatingSystem.IsWindows())
        {
            SetWindowsStartup(enabled);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!enabled)
            {
                if (OwnsLinuxEntry())
                {
                    File.Delete(_linuxEntry);
                }
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_linuxEntry)!);
            File.WriteAllText(_linuxEntry, LinuxEntry());
        }
        else if (enabled)
        {
            throw new PlatformNotSupportedException("Starting with the system is unavailable on this platform.");
        }
    }

    [SupportedOSPlatform("windows")]
    private void SetWindowsStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (enabled)
        {
            key.SetValue("SnowPatcher", "\"" + _executable + "\" --startup", RegistryValueKind.String);
        }
        else if (string.Equals(key.GetValue("SnowPatcher") as string, "\"" + _executable + "\" --startup", StringComparison.OrdinalIgnoreCase))
        {
            key.DeleteValue("SnowPatcher", throwOnMissingValue: false);
        }
    }

    private bool OwnsLinuxEntry() => File.Exists(_linuxEntry) && File.ReadAllText(_linuxEntry) == LinuxEntry();

    private string LinuxEntry()
    {
        var quoted = _executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal);
        return "[Desktop Entry]\nType=Application\nName=SnowPatcher\nExec=\"" + quoted + "\" --startup\nTerminal=false\n";
    }
}
