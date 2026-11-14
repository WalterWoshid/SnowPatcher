using Avalonia;
using SnowPatcher.Core.Updates;

namespace SnowPatcher.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            Environment.ExitCode = args.Length == 2 ? ApplicationUpdateHelper.RunAsync(args[1]).GetAwaiter().GetResult() : 1;
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
