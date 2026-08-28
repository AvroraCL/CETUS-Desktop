using System.IO;
using System.Net.Http;
using Cetus.Configuration;

namespace Cetus.Updates;

internal sealed record UpdateCheckResult(bool UpdateAvailable, ReleaseInfo? Release, string? Error)
{
    public static UpdateCheckResult UpToDate { get; } = new(false, null, null);

    public static UpdateCheckResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Talks to the GitHub Releases update feed: checking for a newer version and
/// downloading the installer with progress and optional checksum verification.
/// Every network failure is normalized into a result instead of an exception.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string DefaultFeedUrl =
        "https://api.github.com/repos/AvroraCL/CETUS-Desktop/releases/latest";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly string _feedUrl;

    public UpdateService(HttpMessageHandler? handler = null, string? feedUrl = null)
    {
        _feedUrl = feedUrl
            ?? ReadEnvironmentFeed()
            ?? DefaultFeedUrl;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = RequestTimeout;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("cetus-desktop-update-check");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The configured feed; CETUS_UPDATE_FEED overrides the GitHub default.</summary>
    public string FeedUrl => _feedUrl;

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(_feedUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"更新服务器返回 {(int)response.StatusCode}。");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            ReleaseInfo? release = UpdateFeed.Parse(json);
            if (release is null)
            {
                return UpdateCheckResult.Failed("更新源返回了无法解析的数据。");
            }

            return release.Version > currentVersion
                ? new UpdateCheckResult(true, release, null)
                : UpdateCheckResult.UpToDate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return UpdateCheckResult.Failed(error.Message);
        }
    }

    /// <summary>
    /// Downloads the release's installer into the update cache directory and
    /// verifies its SHA-256 against SHA256SUMS.txt when that asset exists.
    /// Returns the local installer path.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        ReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ReleaseAsset? installer = UpdateFeed.SelectInstallerAsset(release)
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
