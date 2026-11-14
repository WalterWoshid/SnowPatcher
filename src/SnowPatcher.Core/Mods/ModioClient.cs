using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SnowPatcher.Core.Mods;

// Only public SnowRunner metadata and downloads. No publishing or account endpoints.
public sealed partial class ModioClient
{
    private const string ApiRoot = "https://g-306.modapi.io/v1";
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private long _retryAfterTicks;

    public ModioClient(string? apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey?.Trim() ?? "";
        if (!Hash32().IsMatch(_apiKey))
        {
            _apiKey = "";
        }

        _http = http ?? DefaultClient;
    }

    public bool IsConfigured => _apiKey.Length > 0;
    public bool IsRateLimited => Interlocked.Read(ref _retryAfterTicks) > DateTimeOffset.UtcNow.UtcTicks;

    public async Task<ModioRelease> ResolveAsync(string link, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Host is not ("mod.io" or "www.mod.io"))
        {
            throw new InvalidDataException("Enter a SnowRunner mod.io link.");
        }

        var match = ModLink().Match(uri.AbsolutePath.TrimEnd('/'));
        if (!match.Success)
        {
            throw new InvalidDataException("Enter a link like https://mod.io/g/snowrunner/m/mod-name.");
        }

        var slug = match.Groups[1].Value;
        using var result = await GetJsonAsync($"/games/306/mods?name_id={Uri.EscapeDataString(slug)}&_limit=2", cancellationToken);
        var matches = result.RootElement.GetProperty("data");
        if (matches.GetArrayLength() != 1)
        {
            throw new InvalidDataException("That SnowRunner mod could not be found.");
        }

        return await ReadLiveReleaseAsync(matches[0], cancellationToken);
    }

    public async Task<ModioRelease> GetLatestAsync(long modId, CancellationToken cancellationToken = default)
    {
        if (modId <= 0)
        {
            throw new InvalidDataException("Invalid mod.io mod ID.");
        }

        using var listing = await GetJsonAsync($"/games/306/mods/{modId}", cancellationToken);
        if (listing.RootElement.GetProperty("id").GetInt64() != modId)
        {
            throw new InvalidDataException("mod.io returned a different mod.");
        }

        return await ReadLiveReleaseAsync(listing.RootElement, cancellationToken);
    }

    private async Task<ModioRelease> ReadLiveReleaseAsync(JsonElement listing, CancellationToken cancellationToken)
    {
        if (listing.GetProperty("status").GetInt32() != 1 || listing.GetProperty("visible").GetInt32() != 1 ||
            listing.GetProperty("game_id").GetInt64() != 306)
        {
            throw new InvalidDataException("This mod is not publicly available for SnowRunner.");
        }

        var modId = listing.GetProperty("id").GetInt64();
        var live = listing.GetProperty("platforms").EnumerateArray()
            .Where(p => p.GetProperty("platform").GetString() == "windows").ToArray();
        if (live.Length != 1 || !live[0].TryGetProperty("modfile_live", out var liveId) || liveId.GetInt64() <= 0)
        {
            throw new InvalidDataException("This mod has no live Windows download.");
        }

        using var file = await GetJsonAsync($"/games/306/mods/{modId}/files/{liveId.GetInt64()}", cancellationToken);
        var item = file.RootElement;
        if (item.GetProperty("id").GetInt64() != liveId.GetInt64() || item.GetProperty("mod_id").GetInt64() != modId ||
            !item.GetProperty("platforms").EnumerateArray().Any(p => p.GetProperty("platform").GetString() == "windows" &&
                p.GetProperty("status").GetInt32() == 1))
        {
            throw new InvalidDataException("The Windows file has not been approved.");
        }

        if (item.GetProperty("virus_positive").GetInt32() != 0)
        {
            throw new InvalidDataException("mod.io flagged this download as potentially harmful.");
        }

        var size = item.GetProperty("filesize").GetInt64();
        var md5 = item.GetProperty("filehash").GetProperty("md5").GetString() ?? "";
        if (size <= 0 || size > Packages.PackageArchive.MaxArchiveSize || !Hash32().IsMatch(md5))
        {
            throw new InvalidDataException("The download size or checksum is invalid.");
        }

        var download = new Uri(item.GetProperty("download").GetProperty("binary_url").GetString()!, UriKind.Absolute);
        ValidateDownloadUrl(download);
        string? sha256 = null;
        if (item.TryGetProperty("metadata_blob", out var blob) && blob.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var metadata = JsonDocument.Parse(blob.GetString()!);
                if (metadata.RootElement.ValueKind == JsonValueKind.Object && metadata.RootElement.TryGetProperty("sha256", out var hash) &&
                    hash.ValueKind == JsonValueKind.String && Hash64().IsMatch(hash.GetString() ?? ""))
                {
                    sha256 = hash.GetString();
                }
            }
            catch (JsonException) { /* Other mods may use plain text metadata. */ }
        }
        var source = new ModioSource(modId, liveId.GetInt64(), Label(listing.GetProperty("name").GetString(), "Unnamed mod"),
            Label(item.GetProperty("version").GetString(), "Unknown"), item.GetProperty("date_added").GetInt64(), md5.ToLowerInvariant());
        return new ModioRelease(source, size, sha256, download);
    }

    public async Task<string> DownloadAsync(ModioRelease release, string destination, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireKey();
        var token = cancellationToken;
        var url = release.DownloadUrl;
        HttpResponseMessage? response = null;
        try
        {
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                ValidateDownloadUrl(url);
                using var request = Request(WithKeyForApi(url));
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location ?? throw new IOException("The download redirect is missing.");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    response.Dispose();
                    response = null;
                    continue;
                }
                EnsureSuccess(response);
                break;
            }
            if (response is null)
            {
                throw new IOException("Too many download redirects.");
            }

            if (response.Content.Headers.ContentLength is { } declared && declared != release.Size)
            {
                throw new InvalidDataException("The download size changed. Check for updates again.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[131072];
            long total = 0;
            var previousPercent = -1;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            int count;
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (total > release.Size)
                {
                    throw new InvalidDataException("The download is larger than expected.");
                }

                md5.AppendData(buffer, 0, count);
                sha.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                var percent = (int)(total * 100 / release.Size);
                if (percent != previousPercent)
                {
                    progress?.Report(percent);
                    previousPercent = percent;
                }
            }
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (total != release.Size || !Convert.ToHexStringLower(md5.GetHashAndReset()).Equals(release.Source.Md5, StringComparison.OrdinalIgnoreCase) ||
                (release.Sha256 is not null && !hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The download failed its checksum check. The current mod was kept.");
            }

            await output.FlushAsync(token).ConfigureAwait(false);
            return hash;
        }
        catch (HttpRequestException) { throw new IOException("Could not download from mod.io. Check your connection and try again."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The mod.io download timed out. Try again.");
        }
        finally { response?.Dispose(); }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        RequireKey();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = Request(WithKeyForApi(new Uri(ApiRoot + path)));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024)
            {
                throw new IOException("mod.io returned too much metadata.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024)
                {
                    throw new IOException("mod.io returned too much metadata.");
                }

                output.Write(buffer, 0, count);
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (HttpRequestException) { throw new IOException("Could not reach mod.io. Check your connection and try again."); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("mod.io returned unexpected metadata.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The mod.io check timed out. Try again.");
        }
    }

    private void RequireKey()
    {
        if (!IsConfigured)
        {
            throw new IOException("mod.io is not configured in this build of SnowPatcher.");
        }

        if (IsRateLimited)
        {
            throw new IOException("mod.io is rate limiting requests. Try again later.");
        }
    }

    private Uri WithKeyForApi(Uri url) => url.Host == "g-306.modapi.io"
        ? new Uri(url + (url.Query.Length == 0 ? "?" : "&") + "api_key=" + Uri.EscapeDataString(_apiKey)) : url;

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SnowPatcher", "0.1"));
        request.Headers.Add("X-Modio-Platform", "windows");
        return request;
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            var until = retry?.Date ?? DateTimeOffset.UtcNow.Add(retry?.Delta ?? TimeSpan.FromMinutes(5));
            Interlocked.Exchange(ref _retryAfterTicks, Math.Max(until.UtcTicks, DateTimeOffset.UtcNow.AddSeconds(1).UtcTicks));
        }
        throw new IOException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "SnowPatcher's mod.io key was rejected. This build needs a new key.",
            HttpStatusCode.Forbidden => "mod.io denied access to this mod.",
            HttpStatusCode.NotFound => "This mod or download is no longer available.",
            HttpStatusCode.TooManyRequests => "mod.io is rate limiting requests. Try again later.",
            _ => "mod.io is unavailable. Try again later."
        });
    }

    private static void ValidateDownloadUrl(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
            !(uri.Host == "g-306.modapi.io" || uri.Host.EndsWith(".modcdn.io", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("mod.io returned an untrusted download address.");
        }
    }

    private static string Label(string? value, string fallback)
    {
        var decoded = WebUtility.HtmlDecode(value ?? "");
        var clean = new string(decoded.Where(c => !char.IsControl(c)).Take(200).ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean;
    }

    [GeneratedRegex("^/g/snowrunner/m/([a-zA-Z0-9_-]{1,200})$", RegexOptions.CultureInvariant)]
    private static partial Regex ModLink();
    [GeneratedRegex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hash32();
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Hash64();
}
