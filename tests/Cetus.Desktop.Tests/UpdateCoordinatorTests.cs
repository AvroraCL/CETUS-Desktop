using System.Net;
using System.Net.Http;
using System.Text;
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
