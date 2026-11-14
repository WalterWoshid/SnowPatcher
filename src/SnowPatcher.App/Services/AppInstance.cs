using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SnowPatcher.App.Services;

internal sealed class AppInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _name;
    private readonly object _gate = new();
    private Action? _activate;
    private bool _pendingActivation;

    public AppInstance(string? scope = null)
    {
        var user = Environment.UserDomainName + ":" + Environment.UserName + ":" + scope;
        _name = "snowpatcher-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
        _mutex = new Mutex(false, _name);
        try
        { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary)
        {
            _ = ListenAsync();
        }
    }

    public bool IsPrimary { get; }

    public void SetActivationHandler(Action action)
    {
        lock (_gate)
        {
            _activate = action;
            if (_pendingActivation)
            {
                _pendingActivation = false;
                action();
            }
        }
    }

    public async Task ActivateExistingAsync(bool show)
    {
        using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(show ? "show" : "background").ConfigureAwait(false);
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                if (await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false) == "show")
                {
                    lock (_gate)
                    {
                        if (_activate is { } activate)
                        {
                            activate();
                        }
                        else
                        {
                            _pendingActivation = true;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }
}
