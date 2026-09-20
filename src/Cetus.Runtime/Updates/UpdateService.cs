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

public sealed record UpdateDownloadResult(string Path, UpdateFeedSource Source);

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
/// Multi-source update feed. Checks GitHub and GitCode concurrently, chooses
/// the highest version, and downloads from the preferred source with an
/// automatic same-version fallback.
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
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _client;
    private readonly HttpClient _downloadClient;
    private readonly string _githubFeed;
    private readonly string _gitCodeTags;
    private readonly string _gitCodeReleases;

    public UpdateService(HttpMessageHandler? handler = null, string? githubFeed = null)
        : this(handler, githubFeed, RequestTimeout)
    {
    }

    internal UpdateService(HttpMessageHandler? handler, string? githubFeed, TimeSpan requestTimeout)
    {
        _githubFeed = githubFeed
            ?? ReadEnvironmentFeed()
            ?? DefaultGitHubFeed;
        _gitCodeTags = DefaultGitCodeTags;
        _gitCodeReleases = DefaultGitCodeReleases;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = requestTimeout;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("cetus-desktop-update-check");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        // A separate long-timeout client for release payloads: HttpClient's
        // timeout covers the whole body read even with ResponseHeadersRead,
        // so the 10s check timeout would abort every large download.
        _downloadClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _downloadClient.Timeout = DownloadTimeout;
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("cetus-desktop-update-check");
    }

    /// <summary>The configured GitHub feed; CETUS_UPDATE_FEED overrides it.</summary>
    public string FeedUrl => _githubFeed;

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        string preferredSource,
        CancellationToken cancellationToken)
    {
        UpdateFeedSource preferred = ParseSource(preferredSource);
        Task<FeedResult> githubTask = FetchAsync(UpdateFeedSource.GitHub, cancellationToken);
        Task<FeedResult> gitCodeTask = FetchAsync(UpdateFeedSource.GitCode, cancellationToken);
        FeedResult[] feeds = await Task.WhenAll(githubTask, gitCodeTask);
        cancellationToken.ThrowIfCancellationRequested();

        FeedResult[] available = feeds.Where(feed => feed.Release is not null).ToArray();
        if (available.Length == 0)
        {
            string details = string.Join("；", feeds.Select(feed => $"{SourceName(feed.Source)}：{feed.Error}"));
            return UpdateCheckResult.Failed(details);
        }

        FeedResult selected = available
            .OrderByDescending(feed => feed.Release!.Version)
            .ThenBy(feed => feed.Source == preferred ? 0 : 1)
            .First();
        if (selected.Release!.Version <= currentVersion)
        {
            return UpdateCheckResult.UpToDate(selected.Source);
        }

        return new UpdateCheckResult(
            true,
            selected.Release,
            null,
            selected.Source,
            UpdateCheckResult.ReleasesPageFor(selected.Source));
    }

    private async Task<FeedResult> FetchAsync(UpdateFeedSource source, CancellationToken cancellationToken)
    {
        try
        {
            ReleaseInfo? release = source == UpdateFeedSource.GitHub
                ? await GetGitHubLatestAsync(cancellationToken)
                : await GetGitCodeLatestAsync(cancellationToken);
            return release is null
                ? new FeedResult(source, null, "更新源没有可用版本。")
                : new FeedResult(source, release, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new FeedResult(source, null, error.Message);
        }
    }

    /// <summary>
    /// Races both feeds with a headers-only request and returns the faster
    /// reachable source (exact ties keep the preferred one); null when
    /// neither answers, in which case the caller keeps its original source.
    /// </summary>
    public async Task<UpdateFeedSource?> ProbeFasterSourceAsync(
        UpdateFeedSource preferred,
        CancellationToken cancellationToken)
    {
        Task<(bool Reachable, TimeSpan Elapsed)> github = MeasureAsync(_githubFeed, cancellationToken);
        Task<(bool Reachable, TimeSpan Elapsed)> gitcode = MeasureAsync(_gitCodeTags, cancellationToken);
        await Task.WhenAll(github, gitcode);
        (bool githubOk, TimeSpan githubTime) = github.Result;
        (bool gitcodeOk, TimeSpan gitcodeTime) = gitcode.Result;

        return (githubOk, gitcodeOk) switch
        {
            (true, true) => githubTime < gitcodeTime
                ? UpdateFeedSource.GitHub
                : gitcodeTime < githubTime
                    ? UpdateFeedSource.GitCode
                    : preferred,
            (true, false) => UpdateFeedSource.GitHub,
            (false, true) => UpdateFeedSource.GitCode,
            _ => null,
        };

        async Task<(bool Reachable, TimeSpan Elapsed)> MeasureAsync(string url, CancellationToken token)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using HttpResponseMessage response = await _client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);
                return (response.IsSuccessStatusCode, stopwatch.Elapsed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return (false, TimeSpan.MaxValue);
            }
        }
    }

    /// <summary>
    /// Downloads the release's installer into the update cache directory and
    /// verifies its SHA-256 against SHA256SUMS when that asset exists.
    /// Returns the local installer path.
    /// </summary>
    public Task<string> DownloadInstallerAsync(
        ReleaseInfo release,
        UpdateFeedSource source,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return DownloadBundleAsync(release, source, UpdateFeed.SelectInstallerAsset, progress, cancellationToken);
    }

    /// <summary>Same contract as the installer, for the portable zip bundle.</summary>
    public Task<string> DownloadPortableBundleAsync(
        ReleaseInfo release,
        UpdateFeedSource source,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return DownloadBundleAsync(release, source, UpdateFeed.SelectPortableBundleAsset, progress, cancellationToken);
    }

    public Task<UpdateDownloadResult> DownloadInstallerWithFallbackAsync(
        Version version,
        UpdateFeedSource preferred,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        DownloadWithFallbackAsync(version, preferred, UpdateFeed.SelectInstallerAsset, progress, cancellationToken);

    public Task<UpdateDownloadResult> DownloadPortableBundleWithFallbackAsync(
        Version version,
        UpdateFeedSource preferred,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        DownloadWithFallbackAsync(version, preferred, UpdateFeed.SelectPortableBundleAsset, progress, cancellationToken);

    private async Task<UpdateDownloadResult> DownloadWithFallbackAsync(
        Version version,
        UpdateFeedSource preferred,
        Func<ReleaseInfo, ReleaseAsset?> selectAsset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        // Race the feeds first: a large payload should start on the faster
        // source instead of grinding through the slow one until it fails.
        IReadOnlyList<UpdateFeedSource> order = await OrderedSourcesWithProbeAsync(preferred, cancellationToken);
        foreach (UpdateFeedSource source in order)
        {
            try
            {
                ReleaseInfo release = await ResolveReleaseAsync(source, version, cancellationToken);
                string path = await DownloadResolvedBundleAsync(release, selectAsset, progress, cancellationToken);
                return new UpdateDownloadResult(path, source);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                failures.Add($"{SourceName(source)}：{error.Message}");
            }
        }

        throw new InvalidOperationException($"两个更新源下载均失败：{string.Join("；", failures)}");
    }

    /// <summary>
    /// The preferred source first when the probe cannot answer; otherwise the
    /// faster feed leads and the other remains as the in-place fallback.
    /// </summary>
    private async Task<IReadOnlyList<UpdateFeedSource>> OrderedSourcesWithProbeAsync(
        UpdateFeedSource preferred,
        CancellationToken cancellationToken)
    {
        UpdateFeedSource? faster = null;
        try
        {
            faster = await ProbeFasterSourceAsync(preferred, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Probe failures must never block the download.
        }

        return (faster ?? preferred) == UpdateFeedSource.GitCode
            ? (IReadOnlyList<UpdateFeedSource>)[UpdateFeedSource.GitCode, UpdateFeedSource.GitHub]
            : [UpdateFeedSource.GitHub, UpdateFeedSource.GitCode];
    }

    private async Task<ReleaseInfo> ResolveReleaseAsync(
        UpdateFeedSource source,
        Version version,
        CancellationToken cancellationToken)
    {
        if (source == UpdateFeedSource.GitHub)
        {
            ReleaseInfo? release = await GetGitHubLatestAsync(cancellationToken);
            if (release?.Version == version)
            {
                return release;
            }

            string? tagFeed = BuildGitHubTagFeed(version);
            if (tagFeed is null)
            {
                throw new InvalidOperationException($"GitHub 没有版本 {version} 的发布资源。");
            }

            using HttpResponseMessage response = await _client.GetAsync(tagFeed, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub 没有版本 {version} 的发布资源（HTTP {(int)response.StatusCode}）。");
            }

            ReleaseInfo? exact = UpdateFeed.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return exact?.Version == version
                ? exact
                : throw new InvalidOperationException($"GitHub 返回了错误的版本，期望 {version}。");
        }

        var requested = new ReleaseInfo($"v{version.ToString(3)}", version, null, Array.Empty<ReleaseAsset>());
        return await ResolveGitCodeAssetsAsync(requested, cancellationToken)
            ?? throw new InvalidOperationException($"GitCode 没有版本 {version} 的发布资源。");
    }

    private string? BuildGitHubTagFeed(Version version)
    {
        const string latestSuffix = "/releases/latest";
        return _githubFeed.EndsWith(latestSuffix, StringComparison.OrdinalIgnoreCase)
            ? _githubFeed[..^latestSuffix.Length] + $"/releases/tags/v{version.ToString(3)}"
            : null;
    }

    private async Task<string> DownloadBundleAsync(
        ReleaseInfo release,
        UpdateFeedSource source,
        Func<ReleaseInfo, ReleaseAsset?> selectAsset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ReleaseInfo effective = source == UpdateFeedSource.GitCode
            ? await ResolveGitCodeAssetsAsync(release, cancellationToken) ?? release
            : release;

        return await DownloadResolvedBundleAsync(effective, selectAsset, progress, cancellationToken);
    }

    private async Task<string> DownloadResolvedBundleAsync(
        ReleaseInfo release,
        Func<ReleaseInfo, ReleaseAsset?> selectAsset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {

        ReleaseAsset? installer = selectAsset(release)
            ?? throw new InvalidOperationException("发布中没有找到安装器文件。");

        Directory.CreateDirectory(CetusPaths.UpdateCacheDirectory);
        string targetPath = Path.Combine(CetusPaths.UpdateCacheDirectory, installer.Name);
        try
        {
            await DownloadToFileAsync(installer, targetPath, progress, cancellationToken);
            await VerifyDownloadAsync(release, installer.Name, targetPath, cancellationToken);
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
        using HttpResponseMessage response = await _downloadClient.GetAsync(
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

        string sums = await _downloadClient.GetStringAsync(checksum.DownloadUrl, cancellationToken);
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

    private static IEnumerable<UpdateFeedSource> OrderedSources(string preferred) =>
        OrderedSources(ParseSource(preferred));

    private static IEnumerable<UpdateFeedSource> OrderedSources(UpdateFeedSource preferred)
    {
        yield return preferred;
        yield return preferred == UpdateFeedSource.GitHub ? UpdateFeedSource.GitCode : UpdateFeedSource.GitHub;
    }

    private static UpdateFeedSource ParseSource(string preferred) =>
        preferred.Equals("gitcode", StringComparison.OrdinalIgnoreCase)
            ? UpdateFeedSource.GitCode
            : UpdateFeedSource.GitHub;

    private static string SourceName(UpdateFeedSource source) =>
        source == UpdateFeedSource.GitCode ? "GitCode" : "GitHub";

    private sealed record FeedResult(UpdateFeedSource Source, ReleaseInfo? Release, string? Error);

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

    public void Dispose()
    {
        _client.Dispose();
        _downloadClient.Dispose();
    }
}
