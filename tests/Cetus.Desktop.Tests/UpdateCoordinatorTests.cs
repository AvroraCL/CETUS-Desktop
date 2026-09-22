using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cetus.Configuration;
using Cetus.Updates;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task OverlappingSilentAndInteractiveChecks_RunOnlyOneUpdateTask()
    {
        var handler = new BlockingFeedHandler();
        using var service = new UpdateService(handler);
        string settingsPath = Path.Combine(TestWorkspace.CreateDirectory(), "settings.json");
        string? message = null;
        var coordinator = new UpdateCoordinator(
            owner: null!,
            exitApplication: () => { },
            service: service,
            settings: new CetusSettings(settingsPath),
            currentVersion: new Version(0, 2, 1),
            showInfo: (text, _) => message = text);

        Task silent = coordinator.CheckForUpdatesAsync(interactive: false);
        await handler.BothRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.CheckForUpdatesAsync(interactive: true);
        handler.Release.TrySetResult();
        await silent;

        Assert.Equal("更新任务正在进行，请稍候。", message);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RepeatedChecks_ReplaceAnOlderAvailableRelease()
    {
        var handler = new AdvancingFeedHandler();
        using var service = new UpdateService(handler);
        string settingsPath = Path.Combine(TestWorkspace.CreateDirectory(), "settings.json");
        var coordinator = new UpdateCoordinator(
            owner: null!,
            exitApplication: () => { },
            service: service,
            settings: new CetusSettings(settingsPath),
            currentVersion: new Version(0, 2, 1));

        await coordinator.CheckForUpdatesAsync(interactive: false);
        handler.GitHubTag = "v0.2.3";
        await coordinator.CheckForUpdatesAsync(interactive: false);

        using JsonDocument state = JsonDocument.Parse(coordinator.UpdateStateJson());
        Assert.Equal("v0.2.3", state.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task InPagePortableInstall_DownloadsTheVerifiedBundleOnce()
    {
        string? originalCache = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");
        string cache = TestWorkspace.CreateDirectory();
        Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", cache);
        try
        {
            var handler = new PackageFeedHandler();
            using var service = new UpdateService(handler);
            var settings = new CetusSettings(Path.Combine(cache, "settings.json"));
            UpdateDownloadResult? applied = null;
            var coordinator = new UpdateCoordinator(
                owner: null!,
                exitApplication: () => { },
                service: service,
                settings: settings,
                currentVersion: new Version(0, 2, 1),
                isInstalledEdition: () => false,
                taskbarProgress: _ => { },
                applyPortable: (download, _) => applied = download);

            await coordinator.CheckForUpdatesAsync(interactive: false);
            await coordinator.InstallAvailableAsync();

            Assert.NotNull(applied);
            Assert.Equal(1, handler.PortablePayloadRequests);
            Assert.Equal(applied.Source == UpdateFeedSource.GitCode ? "gitcode" : "github",
                settings.UpdateSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", originalCache);
        }
    }

    [Fact]
    public async Task InPageInstalledUpdate_PersistsTheSourceThatActuallyDownloaded()
    {
        string? originalCache = Environment.GetEnvironmentVariable("CETUS_UPDATE_DIR");
        string cache = TestWorkspace.CreateDirectory();
        Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", cache);
        try
        {
            var handler = new PackageFeedHandler { IncludeGithubInstaller = false };
            using var service = new UpdateService(handler);
            var settings = new CetusSettings(Path.Combine(cache, "settings.json"));
            string? launched = null;
            var coordinator = new UpdateCoordinator(
                owner: null!,
                exitApplication: () => { },
                service: service,
                settings: settings,
                currentVersion: new Version(0, 2, 1),
                isInstalledEdition: () => true,
                taskbarProgress: _ => { },
                launchInstaller: path => launched = path);

            await coordinator.CheckForUpdatesAsync(interactive: false);
            await coordinator.InstallAvailableAsync();

            Assert.NotNull(launched);
            Assert.Equal("gitcode", settings.UpdateSource);
            Assert.Equal(1, handler.InstallerPayloadRequests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", originalCache);
        }
    }

    [Fact]
    public async Task InPageDevelopmentUpdate_CannotDownloadOrInstall()
    {
        string? originalDev = Environment.GetEnvironmentVariable("CETUS_DEV");
        Environment.SetEnvironmentVariable("CETUS_DEV", "1");
        try
        {
            var handler = new PackageFeedHandler();
            using var service = new UpdateService(handler);
            string settingsPath = Path.Combine(TestWorkspace.CreateDirectory(), "settings.json");
            bool applied = false;
            var coordinator = new UpdateCoordinator(
                owner: null!,
                exitApplication: () => { },
                service: service,
                settings: new CetusSettings(settingsPath),
                currentVersion: new Version(0, 2, 1),
                isInstalledEdition: () => false,
                applyPortable: (_, _) => applied = true);

            await coordinator.CheckForUpdatesAsync(interactive: false);
            await coordinator.InstallAvailableAsync();

            using JsonDocument state = JsonDocument.Parse(coordinator.UpdateStateJson());
            Assert.False(state.RootElement.GetProperty("installable").GetBoolean());
            Assert.False(applied);
            Assert.Equal(0, handler.PortablePayloadRequests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CETUS_DEV", originalDev);
        }
    }

    private sealed class PackageFeedHandler : HttpMessageHandler
    {
        private const string InstallerName = "Cetus-Setup-0.2.2.exe";
        private const string PortableName = "Cetus-0.2.2-win-x64-portable.zip";
        private static readonly byte[] Payload = "verified update payload"u8.ToArray();
        private static readonly string PayloadHash =
            Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
        private int _portablePayloadRequests;
        private int _installerPayloadRequests;

        public bool IncludeGithubInstaller { get; set; } = true;
        public int PortablePayloadRequests => Volatile.Read(ref _portablePayloadRequests);
        public int InstallerPayloadRequests => Volatile.Read(ref _installerPayloadRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url == UpdateService.DefaultGitCodeTags)
            {
                return Task.FromResult(Json("""[{"name":"v0.2.1"}]"""));
            }

            if (url == UpdateService.DefaultGitHubFeed)
            {
                return Task.FromResult(Json(ReleaseJson(gitcode: false)));
            }

            if (url == UpdateService.DefaultGitCodeReleases)
            {
                return Task.FromResult(Json($"[{ReleaseJson(gitcode: true)}]"));
            }

            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{PayloadHash}  {InstallerName}\n{PayloadHash}  {PortableName}\n"),
                });
            }

            if (url.EndsWith(InstallerName, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _installerPayloadRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Payload),
                });
            }

            if (url.EndsWith(PortableName, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _portablePayloadRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Payload),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private string ReleaseJson(bool gitcode)
        {
            string baseUrl = gitcode ? "https://gitcode.com/assets/" : "https://github.com/assets/";
            var assets = new List<object>();
            if (gitcode || IncludeGithubInstaller)
            {
                assets.Add(new { name = InstallerName, browser_download_url = baseUrl + InstallerName });
            }

            assets.Add(new { name = PortableName, browser_download_url = baseUrl + PortableName });
            assets.Add(new { name = "SHA256SUMS.txt", browser_download_url = baseUrl + "SHA256SUMS.txt" });
            return JsonSerializer.Serialize(new { tag_name = "v0.2.2", assets });
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class AdvancingFeedHandler : HttpMessageHandler
    {
        public string GitHubTag { get; set; } = "v0.2.2";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string json = request.RequestUri!.Host == "gitcode.com"
                ? """[{"name":"v0.2.1"}]"""
                : $$"""{"tag_name":"{{GitHubTag}}","assets":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BlockingFeedHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource BothRequestsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 2)
            {
                BothRequestsStarted.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
            string json = request.RequestUri!.Host == "gitcode.com"
                ? """[{"name":"v0.2.1"}]"""
                : """{"tag_name":"v0.2.1","assets":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
