using System.Net;
using System.Net.Http;
using System.Text;
using Cetus.Configuration;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class UpdateServiceTests
{
    private static Version Current { get; } = new(0, 2, 0);

    private const string GitHubReleaseJson = """
        {"tag_name":"v0.2.1","body":"- notes","assets":[
            {"name":"Cetus-Setup-0.2.1.exe","browser_download_url":"https://github.com/AvroraCL/CETUS-Desktop/releases/download/v0.2.1/Cetus-Setup-0.2.1.exe","size":15},
            {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/AvroraCL/CETUS-Desktop/releases/download/v0.2.1/SHA256SUMS.txt","size":81}]}
        """;

    private const string GitCodeTagsJson = """[{"name":"v0.2.1"},{"name":"v0.2.0"}]""";

    private const string GitCodeReleasesJson = """
        [{"tag_name":"v0.2.1","body":"- gitcode notes","assets":[
            {"name":"Cetus-Setup-0.2.1.exe","browser_download_url":"https://gitcode.com/HelenaSG/CETUS-Desktop/releases/download/v0.2.1/Cetus-Setup-0.2.1.exe"},
            {"name":"SHA256SUMS.txt","browser_download_url":"https://gitcode.com/HelenaSG/CETUS-Desktop/releases/download/v0.2.1/SHA256SUMS.txt"}]}]
        """;

    private const string InstallerName = "Cetus-Setup-0.2.1.exe";

    private static byte[] InstallerBytes { get; } = "installer-bytes"u8.ToArray();

    private static string InstallerHash =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(InstallerBytes)).ToLowerInvariant();

    private static ReleaseInfo GitHubRelease { get; } = new(
        "v0.2.1",
        new Version(0, 2, 1),
        "- notes",
        [
            new ReleaseAsset(InstallerName, "https://github.com/Cetus-Setup-0.2.1.exe", 15),
            new ReleaseAsset("SHA256SUMS.txt", "https://github.com/SHA256SUMS.txt", 81),
        ]);

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
    public async Task CheckAsync_DetectsNewerReleaseOnGitHub()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Json(GitHubReleaseJson),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, "github", CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(0, 2, 1), result.Release!.Version);
        Assert.Equal(UpdateFeedSource.GitHub, result.Source);
        Assert.Equal(UpdateCheckResult.GitHubReleasesPage, result.ReleasesPageUrl);
        Assert.Equal(UpdateService.DefaultGitHubFeed, handler.Requests[0]);
    }

    [Fact]
    public async Task CheckAsync_TreatsSameVersionAsUpToDate()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Json("""{"tag_name":"v0.2.0","assets":[]}"""),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, "github", CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_FallsBackToGitCodeTagsWhenGitHubFails()
    {
        var handler = new FakeHandler
        {
            Responder = request => request.RequestUri!.Host == "gitcode.com"
                ? Json(GitCodeTagsJson)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(new Version(0, 1, 9), "github", CancellationToken.None);

        Assert.True(result.UpdateAvailable, $"diag: available={result.UpdateAvailable} error={result.Error} src={result.Source}");
        Assert.Equal(UpdateFeedSource.GitCode, result.Source);
        Assert.Equal(UpdateCheckResult.GitCodeReleasesPage, result.ReleasesPageUrl);
        Assert.Equal(UpdateService.DefaultGitCodeTags, handler.Requests[1]);
    }

    [Fact]
    public async Task CheckAsync_PrefersTheRememberedSource()
    {
        var handler = new FakeHandler
        {
            Responder = request => request.RequestUri!.Host == "gitcode.com"
                ? Json(GitCodeTagsJson)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        using var service = new UpdateService(handler);

        await service.CheckAsync(Current, "gitcode", CancellationToken.None);

        Assert.Equal(UpdateService.DefaultGitCodeTags, handler.Requests[0]);
    }

    [Fact]
    public async Task CheckAsync_ReportsWhenAllSourcesFail()
    {
        var handler = new FakeHandler
        {
            Throw = new HttpRequestException("offline"),
        };
        using var service = new UpdateService(handler);

        UpdateCheckResult result = await service.CheckAsync(Current, "github", CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Contains("offline", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FeedUrl_PrefersExplicitValueOverEnvironment()
    {
        string? originalFeed = Environment.GetEnvironmentVariable("CETUS_UPDATE_FEED");
        try
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_FEED", "https://env.test/feed");

            using var explicitService = new UpdateService(githubFeed: "https://explicit.test/feed");
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
            string sums = $"{InstallerHash}  {InstallerName}\n";
            var handler = new FakeHandler
            {
                Responder = request =>
                {
                    string url = request.RequestUri!.ToString();
                    if (url.Contains(UpdateService.DefaultGitCodeReleases, StringComparison.Ordinal))
                    {
                        return Json(GitCodeReleasesJson);
                    }

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
                },
            };
            var fractions = new List<double>();
            var progress = new Progress<double>(fractions.Add);
            using var service = new UpdateService(handler);

            string path = await service.DownloadInstallerAsync(
                GitHubRelease,
                UpdateFeedSource.GitHub,
                progress,
                CancellationToken.None);

            Assert.Equal(Path.Combine(directory.Path, InstallerName), path);
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
    public async Task DownloadInstallerAsync_GitCodeSource_ResolvesAssetsFromReleaseList()
    {
        string? originalDir = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");
        using var directory = new TemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", directory.Path);
            string sums = $"{InstallerHash}  {InstallerName}\n";
            var tagOnlyRelease = new ReleaseInfo(
                "v0.2.1",
                new Version(0, 2, 1),
                null,
                Array.Empty<ReleaseAsset>());
            var handler = new FakeHandler
            {
                Responder = request =>
                {
                    string url = request.RequestUri!.ToString();
                    if (url.Contains(UpdateService.DefaultGitCodeReleases, StringComparison.Ordinal))
                    {
                        return Json(GitCodeReleasesJson);
                    }

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
                },
            };
            using var service = new UpdateService(handler);

            string path = await service.DownloadInstallerAsync(
                tagOnlyRelease,
                UpdateFeedSource.GitCode,
                progress: null,
                CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(path));
            Assert.Contains(UpdateService.DefaultGitCodeReleases, handler.Requests);
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
            string sums = $"{new string('0', 64)}  {InstallerName}\n";
            var handler = new FakeHandler
            {
                Responder = request => Respond(request, sums),
            };
            using var service = new UpdateService(handler);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DownloadInstallerAsync(
                    GitHubRelease,
                    UpdateFeedSource.GitHub,
                    progress: null,
                    CancellationToken.None));

            Assert.False(File.Exists(Path.Combine(directory.Path, InstallerName)));
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
            "v0.2.1",
            new Version(0, 2, 1),
            null,
            [new ReleaseAsset("SHA256SUMS.txt", "https://github.test/SHA256SUMS.txt", 81)]);
        using var service = new UpdateService(new FakeHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadInstallerAsync(
                release,
                UpdateFeedSource.GitHub,
                progress: null,
                CancellationToken.None));
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Json(string body) => JsonResponse(body);

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
        public TemporaryDirectory() => Path = TestWorkspace.CreateDirectory();

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
