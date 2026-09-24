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

    public const string GitHubLatestPage =
        "https://github.com/AvroraCL/CETUS-Desktop/releases/latest";

    public const string DefaultGitCodeTags =
        "https://gitcode.com/api/v5/repos/HelenaSG/CETUS-Desktop/tags";

    public const string DefaultGitCodeReleases =
        "https://gitcode.com/api/v5/repos/HelenaSG/CETUS-Desktop/releases";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly ReleaseDownloader _downloader;
    private readonly string _githubFeed;
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
        _gitCodeReleases = DefaultGitCodeReleases;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = requestTimeout;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("cetus-desktop-update-check");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _downloader = new ReleaseDownloader(handler);
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
        bool publicGitHubFeed = _githubFeed == DefaultGitHubFeed;
        Task<(bool Reachable, TimeSpan Elapsed)> github = MeasureAsync(
            publicGitHubFeed ? GitHubLatestPage : _githubFeed,
            publicGitHubFeed ? HttpMethod.Head : HttpMethod.Get,
            cancellationToken);
        Task<(bool Reachable, TimeSpan Elapsed)> gitcode = MeasureAsync(
            _gitCodeReleases,
            HttpMethod.Get,
            cancellationToken);
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

        async Task<(bool Reachable, TimeSpan Elapsed)> MeasureAsync(
            string url,
            HttpMethod method,
            CancellationToken token)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using var request = new HttpRequestMessage(method, url);
                using HttpResponseMessage response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);
                bool reachable = response.IsSuccessStatusCode ||
                    ((int)response.StatusCode is >= 300 and < 400 &&
                        response.Headers.Location is not null);
                return (reachable, stopwatch.Elapsed);
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
    /// verifies its SHA-256 against the release SHA256SUMS asset. Missing sums fail closed.
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

            HttpResponseMessage response;
            try
            {
                response = await _client.GetAsync(tagFeed, cancellationToken);
            }
            catch (HttpRequestException) when (_githubFeed == DefaultGitHubFeed)
            {
                return await GetGitHubPublicReleaseAsync(version, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _githubFeed == DefaultGitHubFeed)
            {
                return await GetGitHubPublicReleaseAsync(version, cancellationToken);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return await GetGitHubPublicReleaseAsync(version, cancellationToken);
                }

                ReleaseInfo? exact = UpdateFeed.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return exact?.Version == version
                    ? exact
                    : await GetGitHubPublicReleaseAsync(version, cancellationToken);
            }
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

        // Disk-space headroom is enforced by the downloader and produces the
        // actionable 磁盘空间不足 message before any bytes move.

        return await _downloader.DownloadAsync(release, installer, progress, cancellationToken);
    }

    private async Task<ReleaseInfo?> GetGitHubLatestAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(_githubFeed, cancellationToken);
        }
        catch (HttpRequestException) when (_githubFeed == DefaultGitHubFeed)
        {
            return await GetGitHubPublicReleaseAsync(null, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _githubFeed == DefaultGitHubFeed)
        {
            return await GetGitHubPublicReleaseAsync(null, cancellationToken);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (_githubFeed != DefaultGitHubFeed)
                {
                    throw new InvalidOperationException($"更新服务器返回 {(int)response.StatusCode}。");
                }

                return await GetGitHubPublicReleaseAsync(null, cancellationToken);
            }

            ReleaseInfo? release = UpdateFeed.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return release ?? (_githubFeed == DefaultGitHubFeed
                ? await GetGitHubPublicReleaseAsync(null, cancellationToken)
                : null);
        }
    }

    private async Task<ReleaseInfo> GetGitHubPublicReleaseAsync(
        Version? requestedVersion,
        CancellationToken cancellationToken)
    {
        if (_githubFeed != DefaultGitHubFeed)
        {
            throw new InvalidOperationException("自定义 GitHub 更新源没有可用的发布元数据。");
        }

        string pageUrl = requestedVersion is null
            ? GitHubLatestPage
            : $"{UpdateCheckResult.GitHubReleasesPage}/tag/v{requestedVersion.ToString(3)}";
        using var request = new HttpRequestMessage(HttpMethod.Head, pageUrl);
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        bool isRedirect = (int)response.StatusCode is >= 300 and < 400
            && response.Headers.Location is not null;
        if (!response.IsSuccessStatusCode && !isRedirect)
        {
            throw new InvalidOperationException($"GitHub 发布页返回 {(int)response.StatusCode}。");
        }

        Uri? releaseUri = isRedirect
            ? new Uri(new Uri(pageUrl), response.Headers.Location!)
            : response.RequestMessage?.RequestUri;
        const string tagPrefix = "/AvroraCL/CETUS-Desktop/releases/tag/";
        if (releaseUri is null
            || releaseUri.Scheme != Uri.UriSchemeHttps
            || releaseUri.Host != "github.com"
            || !releaseUri.AbsolutePath.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GitHub 发布页没有返回有效的版本标签。");
        }

        string tag = Uri.UnescapeDataString(releaseUri.AbsolutePath[tagPrefix.Length..]);
        if (tag.Contains('/')
            || !UpdateFeed.TryParseTag(tag, out Version version)
            || requestedVersion is not null && version != requestedVersion)
        {
            throw new InvalidOperationException("GitHub 发布页返回了错误的版本标签。");
        }

        string root = $"{UpdateCheckResult.GitHubReleasesPage}/download/{tag}/";
        string installerName = $"Cetus-Setup-{version.ToString(3)}.exe";
        string portableName = $"Cetus-{version.ToString(3)}-win-x64-portable.zip";
        return new ReleaseInfo(
            tag,
            version,
            null,
            [
                new ReleaseAsset(installerName, root + installerName, 0),
                new ReleaseAsset(portableName, root + portableName, 0),
                new ReleaseAsset("SHA256SUMS.txt", root + "SHA256SUMS.txt", 0),
            ]);
    }

    /// <summary>
    /// A pushed tag is not enough to update from GitCode. Only published
    /// releases with a package and checksum can be offered to the user.
    /// </summary>
    private async Task<ReleaseInfo?> GetGitCodeLatestAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(_gitCodeReleases, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"更新服务器返回 {(int)response.StatusCode}。");
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return document.RootElement.EnumerateArray()
            .Select(ParseGitCodeRelease)
            .Where(release => release is not null
                && UpdateFeed.SelectChecksumAsset(release) is not null
                && (UpdateFeed.SelectInstallerAsset(release) is not null
                    || UpdateFeed.SelectPortableBundleAsset(release) is not null))
            .OrderByDescending(release => release!.Version)
            .FirstOrDefault();
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
            ReleaseInfo? candidate = ParseGitCodeRelease(releaseElement);
            if (candidate?.Version != release.Version)
            {
                continue;
            }

            return candidate with { TagName = release.TagName, Notes = candidate.Notes ?? release.Notes };
        }

        return release;
    }

    private static ReleaseInfo? ParseGitCodeRelease(JsonElement releaseElement)
    {
        if (releaseElement.ValueKind != JsonValueKind.Object
            || !releaseElement.TryGetProperty("tag_name", out JsonElement tagElement)
            || tagElement.ValueKind != JsonValueKind.String
            || !UpdateFeed.TryParseTag(tagElement.GetString(), out Version version))
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (releaseElement.TryGetProperty("assets", out JsonElement assetsElement)
            && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsElement.EnumerateArray())
            {
                string? name = asset.ValueKind == JsonValueKind.Object
                    && asset.TryGetProperty("name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : null;
                string? url = asset.ValueKind == JsonValueKind.Object
                    && asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                    && urlElement.ValueKind == JsonValueKind.String
                        ? urlElement.GetString()
                        : null;
                if (name is not null && url is not null)
                {
                    assets.Add(new ReleaseAsset(name, url, 0));
                }
            }
        }

        string? notes = releaseElement.TryGetProperty("body", out JsonElement bodyElement)
            && bodyElement.ValueKind == JsonValueKind.String
            ? bodyElement.GetString()
            : null;
        return new ReleaseInfo(tagElement.GetString()!, version, notes, assets);
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

    public void Dispose()
    {
        _client.Dispose();
        _downloader.Dispose();
    }
}
