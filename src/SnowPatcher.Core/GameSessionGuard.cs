using System.ComponentModel;
using System.Diagnostics;

namespace SnowPatcher.Core;

public static class GameSessionGuard
{
    public static string? GetRestoreBlockReason(GameInstallation installation)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.RootDirectory)) + Path.DirectorySeparatorChar;
        foreach (var name in new[] { "SnowRunner", "SnowRunner.exe" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }

                        if (OperatingSystem.IsWindows())
                        {
                            var executable = process.MainModule?.FileName;
                            if (executable is not null && !Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                        }

                        return "Close SnowRunner before restoring a backup.";
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception)
                    {
                        return "Close SnowRunner before restoring a backup.";
                    }
                }
            }
        }

        return null;
    }

    internal static void EnsureCanRestore(GameInstallation installation)
    {
        if (GetRestoreBlockReason(installation) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
    }

    internal static void EnsureCanApply(GameInstallation installation)
    {
        if (GetRestoreBlockReason(installation) is not null)
        {
            throw new InvalidOperationException("Close SnowRunner before applying mods.");
        }
    }
}
