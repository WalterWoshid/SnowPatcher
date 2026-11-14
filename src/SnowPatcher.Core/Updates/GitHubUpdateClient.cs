using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SnowPatcher.Core.Updates;

public sealed class GitHubUpdateClient
{
    public const string ReleasesPage = "https://github.com/WalterWoshid/SnowPatcher/releases/latest";
    private const string Repository = "WalterWoshid/SnowPatcher";
    private static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;

    public GitHubUpdateClient(HttpClient? client = null) => _client = client ?? SharedClient;

    public static string Platform => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : ""
        : "";

    public async Task<ApplicationRelease?> CheckAsync(CancellationToken token = default)
    {
        if (Platform.Length == 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
        {
            return null;
        }

        var tag = release.GetProperty("tag_name").GetString()!;
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        if (!SnowPatcherVersion.IsValid(version) || SnowPatcherVersion.Compare(version, SnowPatcherVersion.Current) <= 0)
        {
            return null;
        }

        var extension = Platform == "win-x64" ? ".zip" : ".tar.gz";
        var name = $"SnowPatcher-{version}-{Platform}{extension}";
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1)
        {
            throw new IOException("The latest release has no matching SnowPatcher download.");
        }

        var asset = assets[0];
        var digest = asset.GetProperty("digest").GetString();
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var size = asset.GetProperty("size").GetInt64();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdateFiles.IsHash(digest[7..]) ||
            size <= 0 || uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal))
        {
            throw new IOException("GitHub did not provide a verified SnowPatcher download.");
        }

        return new ApplicationRelease(version, Platform, uri, size, digest[7..]);
    }

    public async Task DownloadAsync(ApplicationRelease release, string destination, IProgress<int>? progress, CancellationToken token)
    {
        using var request = Request(release.DownloadUri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        var previousPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            received += read;
            if (received > release.Size)
            {
                throw new IOException("The update download has an unexpected size.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            var percent = (int)(received * 100d / release.Size);
            if (percent != previousPercent)
            {
                progress?.Report(percent);
                previousPercent = percent;
            }
        }

        if (received != release.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
        {
            throw new IOException("The update download failed its SHA-256 check. Nothing was installed.");
        }
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SnowPatcher/" + SnowPatcherVersion.Current);
        return request;
    }
}
