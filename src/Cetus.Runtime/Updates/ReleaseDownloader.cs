using System.IO;
using System.Net.Http;
using Cetus.Configuration;

namespace Cetus.Updates;

/// <summary>
/// The payload side of the update pipeline: streams a release asset to the
/// update cache with the long-timeout download client, verifies its SHA-256
/// against the release sums (fail-closed), and pre-checks disk space so a
/// full volume produces an actionable message instead of a mid-download
/// failure. Feed selection and release resolution live in UpdateService.
/// </summary>
internal sealed class ReleaseDownloader : IDisposable
{
    private readonly HttpClient _downloadClient;
    private readonly bool _ownsClient;
    private bool _disposed;

    public ReleaseDownloader(HttpClient? downloadClient = null)
    {
        _ownsClient = downloadClient is null;
        _downloadClient = downloadClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>Handler-based overload so tests can share one fake handler.</summary>
    public ReleaseDownloader(HttpMessageHandler? handler)
        : this(handler is null ? null : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) })
    {
    }

    /// <summary>
    /// Downloads <paramref name="installer"/> into the update cache and
    /// verifies its SHA-256 against the release sums. Returns the local path.
    /// </summary>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        ReleaseAsset installer,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (installer.Size > 0 && !HasEnoughFreeSpace(CetusPaths.UpdateCacheDirectory, installer.Size * 2))
        {
            throw new InvalidOperationException(
                $"磁盘空间不足：更新需要约 {installer.Size / 1024 / 1024 * 2} MB 可用空间。");
        }

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
            throw new InvalidOperationException(
                $"发布 {release.TagName} 缺少 SHA256SUMS 校验文件，拒绝安装未经验证的更新包。");
        }

        string sums = await _downloadClient.GetStringAsync(checksum.DownloadUrl, cancellationToken);
        string actualHash = UpdateFeed.ComputeFileHash(targetPath);
        if (!UpdateFeed.VerifyChecksum(sums, installerName, actualHash, out string? error))
        {
            throw new InvalidOperationException(error);
        }
    }

    private static bool HasEnoughFreeSpace(string directory, long requiredBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            return root is null
                || new DriveInfo(root).AvailableFreeSpace >= requiredBytes;
        }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException)
        {
            return true; // cannot tell — assume yes and let the write fail
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _downloadClient.Dispose();
        }
    }
}
