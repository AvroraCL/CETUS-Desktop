using System.Net;
using System.Net.Http;
using System.Text;
using Cetus.Configuration;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class UpdateServiceTests
{
    private static Version Current { get; } = new(0, 1, 9);

    private const string ReleaseJson = """
        {
          "tag_name": "v0.2.0",
          "body": "- notes",
          "assets": [
            {"name": "Cetus-Setup-0.2.0.exe", "browser_download_url": "https://feed.test/Cetus-Setup-0.2.0.exe", "size": 15},
            {"name": "SHA256SUMS.txt", "browser_download_url": "https://feed.test/SHA256SUMS.txt", "size": 81}
          ]
        }
        """;

    private static ReleaseInfo Release { get; } = new(
        "v0.2.0",
        new Version(0, 2, 0),
        "- notes",
        [
            new ReleaseAsset("Cetus-Setup-0.2.0.exe", "https://feed.test/Cetus-Setup-0.2.0.exe", 15),
            new ReleaseAsset("SHA256SUMS.txt", "https://feed.test/SHA256SUMS.txt", 81),
        ]);

    private static byte[] InstallerBytes { get; } = "installer-bytes"u8.ToArray();

    private static string InstallerHash =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(InstallerBytes)).ToLowerInvariant();

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string?> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        public Exception? Throw { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString());
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Responder!(request));
        }
    }

    [Fact]
    public async Task CheckAsync_DetectsNewerRelease()
    {
        var handler = new FakeHandler
        {
            Responder = _ => JsonResponse(ReleaseJson),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.NotNull(result.Release);
        Assert.Equal(new Version(0, 2, 0), result.Release!.Version);
        Assert.Null(result.Error);
        Assert.Equal(UpdateService.DefaultFeedUrl, handler.Requests.Single());
        Assert.NotEmpty(handler.Requests[0]!);
    }

    [Fact]
    public async Task CheckAsync_TreatsSameVersionAsUpToDate()
    {
        string upToDateJson = ReleaseJson.Replace("v0.2.0", "v0.1.9");
        var handler = new FakeHandler
        {
            Responder = _ => JsonResponse(upToDateJson),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_ReportsNetworkFailures()
    {
        var handler = new FakeHandler
        {
            Throw = new HttpRequestException("boom"),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Contains("boom", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_ReportsHttpErrors()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Contains("404", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FeedUrl_PrefersExplicitValueOverEnvironment()
    {
        string? originalFeed = Environment.GetEnvironmentVariable("CETUS_UPDATE_FEED");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_FEED", "https://env.test/feed");

            using var explicitService = new UpdateService(feedUrl: "https://explicit.test/feed");
            Assert.Equal("https://explicit.test/feed", explicitService.FeedUrl);

            using var envService = new UpdateService();
            Assert.Equal("https://env.test/feed", envService.FeedUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_FEED", originalFeed);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_WritesVerifiedFileAndReportsProgress()
    {
        string? originalDir = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");
        using var directory = new TemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", directory.Path);
            string sums = $"{InstallerHash}  Cetus-Setup-0.2.0.exe\n";
            var handler = new FakeHandler
            {
                Responder = request => Respond(request, sums),
            };
            var fractions = new List<double>();
            var progress = new Progress<double>(fractions.Add);
            using var service = new UpdateService(handler);

            string path = await service.DownloadInstallerAsync(Release, progress, CancellationToken.None);

            Assert.Equal(Path.Combine(directory.Path, "Cetus-Setup-0.2.0.exe"), path);
            Assert.True(File.Exists(path));
            Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(1.0, fractions[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", originalDir);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_DeletesFileOnChecksumMismatch()
    {
        string? originalDir = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");
        using var directory = new TemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", directory.Path);
            string sums = $"{new string('0', 64)}  Cetus-Setup-0.2.0.exe\n";
            var handler = new FakeHandler
            {
                Responder = request => Respond(request, sums),
            };
            using var service = new UpdateService(handler);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DownloadInstallerAsync(Release, progress: null, CancellationToken.None));

            Assert.False(File.Exists(Path.Combine(directory.Path, "Cetus-Setup-0.2.0.exe")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", originalDir);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_ThrowsWhenInstallerAssetMissing()
    {
        var release = new ReleaseInfo(
            "v0.2.0",
            new Version(0, 2, 0),
            null,
            [new ReleaseAsset("SHA256SUMS.txt", "https://feed.test/SHA256SUMS.txt", 81)]);
        using var service = new UpdateService(new FakeHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadInstallerAsync(release, progress: null, CancellationToken.None));
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Respond(HttpRequestMessage request, string sums)
    {
        string url = request.RequestUri!.ToString();
        if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sums, Encoding.UTF8, "text/plain"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(InstallerBytes),
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

        public string Path { get; }

        public void Dispose()
        {
            if (TestWorkspace.RetainArtifacts) return;
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Leave failed-test artifacts for diagnosis.
            }
        }
    }
}
