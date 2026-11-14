using Avalonia;
using SnowPatcher.App.Services;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App;

internal static class Program
{
    internal static AppInstance? Instance { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        using var setupGuard = OperatingSystem.IsWindows() ? new Mutex(false, @"Local\SnowPatcher.Desktop") : null;
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            Environment.ExitCode = args.Length == 2 ? ApplicationUpdateHelper.RunAsync(args[1]).GetAwaiter().GetResult() : 1;
            return;
        }

        using var instance = new AppInstance();
        if (!instance.IsPrimary)
        {
            try
            { instance.ActivateExistingAsync(!args.Contains("--startup", StringComparer.Ordinal) && !args.Contains("--background", StringComparer.Ordinal)).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { Environment.ExitCode = 1; }
            return;
        }
        Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
