using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinCoreManager.Services.SelfUpdate;

internal enum UpdateState
{
    Idle, Checking, NotConfigured, RepositoryUnavailable, ReleaseUnavailable, Current, Available, LocalBuild, OperationActive,
    RateLimited, Network, Timeout, InvalidResponse, IncompatiblePackage, VerificationFailed,
    Downloading, Canceled, InsufficientSpace, CannotWrite, Ready, Failed,
}

internal sealed record UpdateOffer(UpdateVersion Version, string Notes, Uri ReleasePage, Uri Download, long Size, string Digest, DateTimeOffset? PublishedAt = null);
internal sealed record UpdateCheck(UpdateState State, UpdateOffer? Offer = null, Uri? ReleasePage = null);

internal sealed partial class GitHubReleases(HttpClient http, string? repository, long? repositoryId, TimeSpan checkTimeout, TimeSpan downloadTimeout, bool officialBuild = true)
{
    internal static readonly string? OfficialRepository = "rsehenth/WinCore-Manager";
    internal static readonly long? OfficialRepositoryId = 1472964599;
    internal static bool OfficialIdentityConfigured => OfficialRepository is not null && OfficialRepositoryId is > 0;
    internal const long MaximumZipBytes = 512L * 1024 * 1024;
    private const int MaximumJsonBytes = 1024 * 1024;

    internal static GitHubReleases Create()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders =
            {
                UserAgent = { new ProductInfoHeaderValue("WinCoreManager", InstalledVersion.ToString()) },
                Accept = { new MediaTypeWithQualityHeaderValue("application/vnd.github+json") },
            },
        };
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubReleases(client, OfficialRepository,
            OfficialRepositoryId, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(10), IsPublishedBuild);
    }

    internal static UpdateVersion InstalledVersion { get; } = UpdateVersion.FromAssembly(typeof(GitHubReleases).Assembly);
    internal static bool IsPublishedBuild { get; } = typeof(GitHubReleases).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "WinCoreBuildChannel" && a.Value == "Published");
    internal bool IsConfigured => repository is not null;

    public async Task<UpdateCheck> CheckAsync(UpdateVersion installed, CancellationToken cancellationToken)
    {
        if (repository is null)
        {
            return new(UpdateState.NotConfigured);
        }

        if (!RepositoryPattern().IsMatch(repository) || repositoryId is not > 0)
        {
            return new(UpdateState.RepositoryUnavailable);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(checkTimeout);
        var token = deadline.Token;
        try
        {
            var api = new Uri($"https://api.github.com/repos/{repository}/");
            using var identity = await http.GetAsync(api.AbsoluteUri.TrimEnd('/'), HttpCompletionOption.ResponseHeadersRead, token);
            if (identity.StatusCode == HttpStatusCode.NotFound)
            {
                return new(UpdateState.RepositoryUnavailable);
            }

            if (MapStatus(identity) is { } identityFailure)
            {
                return new(identityFailure);
            }

            using var repoJson = await ReadJsonAsync(identity.Content, token);
            if (!string.Equals(JsonString(repoJson.RootElement, "full_name"), repository, StringComparison.OrdinalIgnoreCase) ||
                !repoJson.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var actualId) || actualId != repositoryId ||
                !repoJson.RootElement.TryGetProperty("private", out var isPrivate) || isPrivate.ValueKind != JsonValueKind.False ||
                repoJson.RootElement.TryGetProperty("fork", out var fork) && fork.ValueKind != JsonValueKind.False)
            {
                return new(UpdateState.RepositoryUnavailable);
            }

            var releasesPage = new Uri($"https://github.com/{repository}/releases");
            using var response = await http.GetAsync(new Uri(api, "releases/latest"), HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(UpdateState.ReleaseUnavailable, ReleasePage: releasesPage);
            }

            if (MapStatus(response) is { } releaseFailure)
            {
                return new(releaseFailure, ReleasePage: releasesPage);
            }

            using var release = await ReadJsonAsync(response.Content, token);
            return InterpretRelease(release.RootElement, installed, releasesPage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateState.Timeout);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new(UpdateState.InvalidResponse);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(UpdateState.Network);
        }
    }

    private UpdateCheck InterpretRelease(JsonElement release, UpdateVersion installed, Uri releasesPage)
    {
        if (!release.TryGetProperty("draft", out var draft) || !release.TryGetProperty("prerelease", out var prerelease) ||
            draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !UpdateVersion.TryParse(JsonString(release, "tag_name"), out var version))
        {
            return new(UpdateState.InvalidResponse, ReleasePage: releasesPage);
        }

        if (draft.GetBoolean() || prerelease.GetBoolean() || !version.IsStable)
        {
            return new(UpdateState.ReleaseUnavailable, ReleasePage: releasesPage);
        }

        if (version.CompareTo(installed) <= 0)
        {
            return new(officialBuild ? UpdateState.Current : UpdateState.LocalBuild, ReleasePage: releasesPage);
        }

        var tag = Uri.EscapeDataString(JsonString(release, "tag_name")!);
        var releasePage = new Uri($"{releasesPage}/tag/{tag}");
        var assetName = $"WinCoreManager-{version}-win-x64-portable.zip";
        var download = new Uri($"{releasesPage}/download/{tag}/{assetName}");
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return new(UpdateState.InvalidResponse, ReleasePage: releasePage);
        }

        var candidates = assets.EnumerateArray().Where(asset => JsonString(asset, "name") == assetName).ToArray();
        if (candidates.Length != 1)
        {
            return new(UpdateState.IncompatiblePackage, ReleasePage: releasePage);
        }

        var entry = candidates[0];
        if (JsonString(entry, "state") != "uploaded" ||
            JsonString(entry, "browser_download_url") != download.AbsoluteUri ||
            JsonString(entry, "content_type") is not ("application/zip" or "application/x-zip-compressed" or "application/octet-stream") ||
            !entry.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out var sizeBytes) || sizeBytes is <= 0 or > MaximumZipBytes)
        {
            return new(UpdateState.IncompatiblePackage, ReleasePage: releasePage);
        }

        var digest = JsonString(entry, "digest");
        if (digest is null || !DigestPattern().IsMatch(digest))
        {
            return new(UpdateState.VerificationFailed, ReleasePage: releasePage);
        }

        var notes = JsonString(release, "body") ?? "";
        var date = release.TryGetProperty("published_at", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var parsed)
            ? parsed : (DateTimeOffset?)null;
        return new(UpdateState.Available,
            new UpdateOffer(version, notes.Length > 12_000 ? notes[..12_000] : notes, releasePage, download, sizeBytes, digest[7..], date),
            releasePage);
    }

    public async Task DownloadAsync(UpdateOffer offer, string path, IProgress<double> progress, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(downloadTimeout);
        var token = deadline.Token;
        try
        {
            using var response = await OpenAssetAsync(offer.Download, token);
            if (response.StatusCode != HttpStatusCode.OK ||
                (response.Content.Headers.ContentLength is long length && length != offer.Size))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }

            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                total += read;
                if (total > offer.Size)
                {
                    throw new UpdateException(UpdateState.VerificationFailed);
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                progress.Report((double)total / offer.Size);
            }

            if (total != offer.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(offer.Digest)))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }

            await output.FlushAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateState.Timeout);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateState.Network, ex);
        }
    }

    internal async Task DownloadSignatureAsync(UpdateOffer offer, string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(checkTimeout);
        using var response = await OpenAssetAsync(new Uri(offer.Download.AbsoluteUri + ".sig"), deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[65];
        if (await input.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token) != 64)
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes.AsMemory(0, 64), deadline.Token);
        output.Flush(flushToDisk: true);
    }

    private async Task<HttpResponseMessage> OpenAssetAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects < 4; redirects++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps ||
                !location.IsDefaultPort || location.UserInfo.Length != 0 ||
                location.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            {
                break;
            }

            uri = location;
        }

        throw new UpdateException(UpdateState.VerificationFailed);
    }

    private static UpdateState? MapStatus(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.OK => null,
        HttpStatusCode.TooManyRequests => UpdateState.RateLimited,
        HttpStatusCode.Forbidden when response.Headers.RetryAfter is not null ||
            (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")) => UpdateState.RateLimited,
        HttpStatusCode.Forbidden => UpdateState.RepositoryUnavailable,
        _ => UpdateState.Network,
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaximumJsonBytes)
        {
            throw new JsonException();
        }

        await using var input = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > MaximumJsonBytes)
            {
                throw new JsonException();
            }
            buffer.Write(chunk, 0, count);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: token);
    }

    private static string? JsonString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_-][A-Za-z0-9_.-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"\Asha256:[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}

internal sealed class UpdateException(UpdateState state, Exception? inner = null) : Exception(state.ToString(), inner)
{
    public UpdateState State { get; } = state;
}
