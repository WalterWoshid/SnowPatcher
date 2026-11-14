namespace SnowPatcher.App.Services.Desktop;

public interface IDesktopIntegration : IDisposable
{
    bool SetBackgroundEnabled(bool enabled);
    void Notify(string title, string message);
}
