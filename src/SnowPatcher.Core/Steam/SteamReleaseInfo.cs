using System.Globalization;
using SteamKit2;

namespace SnowPatcher.Core.Steam;

public static class SteamReleaseInfo
{
    public static async Task<DateTimeOffset?> GetReleaseTimeAsync(string installedBuildId, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(installedBuildId, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedBuild) || expectedBuild == 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var client = new SteamClient(SteamConfiguration.Create(builder => builder
            .WithProtocolTypes(ProtocolTypes.WebSocket)
            .WithConnectionTimeout(TimeSpan.FromSeconds(8))));
        var callbacks = new CallbackManager(client);
        var user = client.GetHandler<SteamUser>()!;
        var apps = client.GetHandler<SteamApps>()!;
        var completion = new TaskCompletionSource<DateTimeOffset?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = Task.CompletedTask;

        using var connected = callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => user.LogOnAnonymous());
        using var disconnected = callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => completion.TrySetResult(null));
        using var loggedOn = callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            if (callback.Result != EResult.OK)
            {
                completion.TrySetResult(null);
                return;
            }

            lookup = CompleteLookupAsync(apps, expectedBuild, completion, timeout.Token);
        });

        try
        {
            client.Connect();
            while (!completion.Task.IsCompleted)
            {
                await callbacks.RunWaitCallbackAsync(timeout.Token).ConfigureAwait(false);
            }
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            client.Disconnect();
            await lookup.ConfigureAwait(false);
        }
    }

    private static async Task CompleteLookupAsync(SteamApps apps, ulong expectedBuild,
        TaskCompletionSource<DateTimeOffset?> completion, CancellationToken cancellationToken)
    {
        try
        {
            var appId = uint.Parse(SteamInstallationLocator.AppId, CultureInfo.InvariantCulture);
            var access = await apps.PICSGetAccessTokens([appId], []).ToTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = new SteamApps.PICSRequest(appId);
            if (access.AppTokens.TryGetValue(appId, out var token))
            {
                request.AccessToken = token;
            }

            var response = await apps.PICSGetProductInfo([request], []).ToTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var product = response.Results?.SelectMany(result => result.Apps).FirstOrDefault(pair => pair.Key == appId).Value;
            if (product is null)
            {
                completion.TrySetResult(null);
                return;
            }

            var branch = product.KeyValues["depots"]["branches"]["public"];
            var publicBuild = branch["buildid"].AsUnsignedLong();
            var publishedAt = branch["timeupdated"].AsUnsignedLong();

            // A branch's timestamp applies only to its current build, never to an older installation.
            completion.TrySetResult(publicBuild == expectedBuild && publishedAt > 0 && publishedAt <= (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds((long)publishedAt)
                : null);
        }
        catch (OperationCanceledException)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Optional metadata must not fault an async callback or hide the installed build.
            completion.TrySetResult(null);
        }
    }
}
