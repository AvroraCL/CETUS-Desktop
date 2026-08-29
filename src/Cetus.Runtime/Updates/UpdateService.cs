using System.IO;
using System.Net.Http;
using System.Text.Json;
using Cetus.Configuration;

namespace Cetus.Updates;

public enum UpdateFeedSource
{
    GitHub,
    GitCode,
}

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    ReleaseInfo? Release,
    string? Error,
    UpdateFeedSource Source,
    string ReleasesPageUrl)
{
    public static UpdateCheckResult UpToDate(UpdateFeedSource source) =>
        new(false, null, null, source, ReleasesPageFor(source));

    public static UpdateCheckResult Failed(string error) =>
        new(false, null, error, UpdateFeedSource.GitHub, ReleasesPageFor(UpdateFeedSource.GitHub));

    public static string ReleasesPageFor(UpdateFeedSource source) => source switch
    {
        UpdateFeedSource.GitCode => GitCodeReleasesPage,
        _ => GitHubReleasesPage,
    };

    public const string GitHubReleasesPage = "https://github.com/AvroraCL/CETUS-Desktop/releases";
    public const string GitCodeReleasesPage = "https://gitcode.com/HelenaSG/CETUS-Desktop/releases";
}

/// <summary>
/// Multi-source update feed: GitHub first, GitCode as the fallback when
/// GitHub is unreachable. The last source that answered is remembered in
/// settings so later checks try it first. Every network failure degrades to
/// the next source instead of aborting.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string DefaultGitHubFeed =
        "https://api.github.com/repos/AvroraCL/CETUS-Desktop/releases/latest";

    public const string DefaultGitCodeTags =
        "https://gitcode.com/api/v5/repos/HelenaSG/CETUS-Desktop/tags";

    public const string DefaultGitCodeReleases =
        "https://gitcode.com/api/v5/repos/HelenaSG/CETUS-Desktop/releases";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly string _githubFeed;
    private readonly string _gitCodeTags;
    private readonly string _gitCodeReleases;

    public UpdateService(HttpMessageHandler? handler = null, string? githubFeed = null)
    {
        _githubFeed = githubFeed
            ?? ReadEnvironmentFeed()
            ?? DefaultGitHubFeed;
        _gitCodeTags = DefaultGitCodeTags;
        _gitCodeReleases = DefaultGitCodeReleases;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = RequestTimeout;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("cetus-desktop-update-check");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The configured GitHub feed; CETUS_UPDATE_FEED overrides it.</summary>
    public string FeedUrl => _githubFeed;

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        string preferredSource,
        CancellationToken cancellationToken)
    {
        string? lastError = null;
        foreach (UpdateFeedSource source in OrderedSources(preferredSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReleaseInfo? release = source switch
                {
                    UpdateFeedSource.GitHub => await GetGitHubLatestAsync(cancellationToken),
                    _ => await GetGitCodeLatestAsync(cancellationToken),
                };

                if (release is null)
                {
                    lastError = "更新源没有可用版本。";
                    continue;
                }

                if (release.Version <= currentVersion)
                {
                    return UpdateCheckResult.UpToDate(source);
                }

                return new UpdateCheckResult(true, release, null, source, UpdateCheckResult.ReleasesPageFor(source));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error.Message;
            }
        }

        return UpdateCheckResult.Failed(lastError ?? "更新源不可用。");
    }

    /// <summary>
    /// Downloads the release's installer into the update cache directory and
    /// verifies its SHA-256 against SHA256SUMS when that asset exists.
    /// Returns the local installer path.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        ReleaseInfo release,
        UpdateFeedSource source,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ReleaseInfo effective = source == UpdateFeedSource.GitCode
            ? await ResolveGitCodeAssetsAsync(release, cancellationToken) ?? release
            : release;

        ReleaseAsset? installer = UpdateFeed.SelectInstallerAsset(effective)
            ?? throw new InvalidOperationException("发布中没有找到安装器文件。");

        Directory.CreateDirectory(CetusPaths.UpdateCacheDirectory);
        string targetPath = Path.Combine(CetusPaths.UpdateCacheDirectory, installer.Name);
        try
        {
            await DownloadToFileAsync(installer, targetPath, progress, cancellationToken);
            await VerifyDownloadAsync(effective, installer.Name, targetPath, cancellationToken);
            return targetPath;
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
    }

    private async Task<ReleaseInfo?> GetGitHubLatestAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(_githubFeed, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"更新服务器返回 {(int)response.StatusCode}。");
        }

        return UpdateFeed.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// GitCode does not mirror GitHub's releases/latest: the newest known
    /// version comes from the tag list, and release assets are resolved from
    /// the releases list only when a download is actually requested.
    /// </summary>
    private async Task<ReleaseInfo?> GetGitCodeLatestAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(_gitCodeTags, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"更新服务器返回 {(int)response.StatusCode}。");
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Version? best = null;
        string? bestTag = null;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tag in document.RootElement.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.Object
                    && tag.TryGetProperty("name", out var nameElement)
                    && UpdateFeed.TryParseTag(nameElement.GetString(), out Version version)
                    && (best is null || version > best))
                {
                    best = version;
                    bestTag = nameElement.GetString();
                }
            }
        }

        return bestTag is null ? null : new ReleaseInfo(bestTag, best!, null, Array.Empty<ReleaseAsset>());
    }

    private async Task<ReleaseInfo?> ResolveGitCodeAssetsAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(_gitCodeReleases, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return release;
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return release;
        }

        foreach (JsonElement releaseElement in document.RootElement.EnumerateArray())
        {
            if (releaseElement.ValueKind != JsonValueKind.Object
                || !releaseElement.TryGetProperty("tag_name", out var tagElement)
                || !UpdateFeed.TryParseTag(tagElement.GetString(), out Version version)
                || version != release.Version)
            {
                continue;
            }

            var assets = new List<ReleaseAsset>();
            if (releaseElement.TryGetProperty("assets", out var assetsElement)
                && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assetsElement.EnumerateArray())
                {
                    string? name = asset.ValueKind == JsonValueKind.Object
                        && asset.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString()
                            : null;
                    string? url = asset.ValueKind == JsonValueKind.Object
                        && asset.TryGetProperty("browser_download_url", out var urlElement)
                            ? urlElement.GetString()
                            : null;
                    if (name is not null && url is not null)
                    {
                        assets.Add(new ReleaseAsset(name, url, 0));
                    }
                }
            }

            string? notes = releaseElement.TryGetProperty("body", out var bodyElement)
                && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : release.Notes;
            return new ReleaseInfo(release.TagName, release.Version, notes, assets);
        }

        return release;
    }

    private async Task DownloadToFileAsync(
        ReleaseAsset asset,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            asset.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        long? totalLength = response.Content.Headers.ContentLength;

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = File.Create(targetPath);
        byte[] buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            if (totalLength is > 0)
            {
                progress?.Report(Math.Min(1.0, (double)totalRead / totalLength.Value));
            }
        }

        progress?.Report(1.0);
    }

    private async Task VerifyDownloadAsync(
        ReleaseInfo release,
        string installerName,
        string targetPath,
        CancellationToken cancellationToken)
    {
        ReleaseAsset? checksum = UpdateFeed.SelectChecksumAsset(release);
        if (checksum is null)
        {
            // Older releases ship no sums file; accept the HTTPS-only download.
            return;
        }

        string sums = await _client.GetStringAsync(checksum.DownloadUrl, cancellationToken);
        string actualHash = UpdateFeed.ComputeFileHash(targetPath);
        if (!UpdateFeed.VerifyChecksum(sums, installerName, actualHash, out string? error))
        {
            throw new InvalidOperationException(error);
        }
    }

    private static string? ReadEnvironmentFeed()
    {
        string? value = Environment.GetEnvironmentVariable("CETUS_UPDATE_FEED");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static IEnumerable<UpdateFeedSource> OrderedSources(string preferred)
    {
        bool gitCodeFirst = preferred.Equals("gitcode", StringComparison.OrdinalIgnoreCase);
        if (gitCodeFirst)
        {
            yield return UpdateFeedSource.GitCode;
        }

        yield return UpdateFeedSource.GitHub;
        if (!gitCodeFirst)
        {
            yield return UpdateFeedSource.GitCode;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A failed download must not mask its own error.
        }
    }

    public void Dispose() => _client.Dispose();
}
