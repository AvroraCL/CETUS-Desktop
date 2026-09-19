using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cetus.DshStatus;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshSessionWatcherTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4302/");

    [Fact]
    public async Task FirstPoll_BuildsBaselineWithoutEvents()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: true)));
        using var watcher = CreateWatcher(handler);

        List<DshAgentFinishedEventArgs> events = [];
        watcher.AgentFinished += (_, e) => events.Add(e);

        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task RunningToIdle_RaisesFinishedEventWithSessionTitle()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: true)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        using var watcher = CreateWatcher(handler);

        List<DshAgentFinishedEventArgs> events = [];
        watcher.AgentFinished += (_, e) => events.Add(e);

        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);

        DshAgentFinishedEventArgs finished = Assert.Single(events);
        Assert.Equal("s1", finished.SessionId);
        Assert.Equal("任务一", finished.Title);
    }

    [Fact]
    public async Task RepeatCompletion_WithinCooldown_IsSuppressed()
    {
        MutableTimeProvider time = new();
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: true)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: true)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        using var watcher = CreateWatcher(handler, time, cooldownMinutes: 2);

        List<DshAgentFinishedEventArgs> events = [];
        watcher.AgentFinished += (_, e) => events.Add(e);

        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));

        // Second completion inside the cooldown window stays silent, even
        // after another run/idle cycle.
        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Single(events);

        time.Advance(TimeSpan.FromMinutes(2));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: true)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task IdleToIdle_DoesNotRaiseEvents()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        using var watcher = CreateWatcher(handler);

        List<DshAgentFinishedEventArgs> events = [];
        watcher.AgentFinished += (_, e) => events.Add(e);

        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task NewSessionStartingLater_IsReportedOnCompletion()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(SessionsJson(("s1", "旧会话", Running: false)));
        handler.Responses.Enqueue(SessionsJson(("s1", "旧会话", Running: false), ("s2", "新会话", Running: true)));
        handler.Responses.Enqueue(SessionsJson(("s1", "旧会话", Running: false), ("s2", "新会话", Running: false)));
        using var watcher = CreateWatcher(handler);

        List<DshAgentFinishedEventArgs> events = [];
        watcher.AgentFinished += (_, e) => events.Add(e);

        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);

        DshAgentFinishedEventArgs finished = Assert.Single(events);
        Assert.Equal("s2", finished.SessionId);
        Assert.Equal("新会话", finished.Title);
    }

    private static DshSessionWatcher CreateWatcher(
        FakeDshHandler handler,
        MutableTimeProvider? time = null,
        double cooldownMinutes = 2)
    {
        return new DshSessionWatcher(
            new DshSessionClient(handler: handler),
            () => Endpoint,
            timeProvider: time ?? new MutableTimeProvider(),
            pollInterval: TimeSpan.FromSeconds(10),
            completionCooldown: TimeSpan.FromMinutes(cooldownMinutes));
    }

    private static HttpResponseMessage SessionsJson(params (string Id, string Title, bool Running)[] sessions)
    {
        object payload = new
        {
            result = new
            {
                ok = true,
                value = new
                {
                    items = sessions.Select(session => (object)new
                    {
                        sessionId = session.Id,
                        cwd = @"F:\repos\demo",
                        running = session.Running,
                        projections = new
                        {
                            values = new { title = session.Title },
                        },
                    }),
                },
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class FakeDshHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                Responses.Count > 0
                    ? Responses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
