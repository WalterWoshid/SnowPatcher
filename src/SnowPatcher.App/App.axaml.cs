using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SnowPatcher.App.Services;
using SnowPatcher.App.Services.Desktop;
using SnowPatcher.App.ViewModels;
using SnowPatcher.App.Views;

namespace SnowPatcher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            var preferences = new PreferencesService();
            var window = new MainWindow(new MainWindowViewModel(preferences: preferences));
            var session = new DesktopSession(window, preferences, desktop);
            Program.Instance?.SetActivationHandler(() => Dispatcher.UIThread.Post(session.Show));
            desktop.Startup += (_, args) => Dispatcher.UIThread.Post(async () =>
            {
                desktop.MainWindow = window;
                await session.StartAsync(args.Args);
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
