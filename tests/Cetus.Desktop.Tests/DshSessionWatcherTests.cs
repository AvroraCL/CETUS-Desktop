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
    public async Task FaultySubscriber_DoesNotBreakDispatchToOthers()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: true)));
        handler.Responses.Enqueue(SessionsJson(("s1", "任务一", Running: false)));
        using var watcher = CreateWatcher(handler);

        watcher.AgentFinished += (_, _) => throw new InvalidOperationException("boom");
        List<DshAgentFinishedEventArgs> events = [];
        watcher.AgentFinished += (_, e) => events.Add(e);

        // Must not throw despite the faulty first subscriber.
        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);

        DshAgentFinishedEventArgs finished = Assert.Single(events);
        Assert.Equal("s1", finished.SessionId);
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

    [Fact]
    public async Task PollLoop_RequestTimeoutKeepsStateAndLaterReportsCompletion()
    {
        var handler = new TimeoutThenSessionsHandler();
        var logs = new List<string>();
        using var watcher = new DshSessionWatcher(
            new DshSessionClient(handler: handler),
            () => Endpoint,
            pollInterval: TimeSpan.FromMilliseconds(15),
            completionCooldown: TimeSpan.FromMinutes(2),
            log: message => logs.Add(message));
        var completed = new TaskCompletionSource<DshAgentFinishedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.AgentFinished += (_, args) => completed.TrySetResult(args);

        watcher.Start();
        DshAgentFinishedEventArgs result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("s1", result.SessionId);
        Assert.Contains(logs, entry => entry.Contains("polling failed", StringComparison.Ordinal));
        Assert.Contains(logs, entry => entry.Contains("recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_CancelsAnActivePollImmediately()
    {
        var handler = new CancellationAwareHandler();
        var watcher = new DshSessionWatcher(
            new DshSessionClient(handler: handler),
            () => Endpoint,
            pollInterval: TimeSpan.FromMilliseconds(10));

        watcher.Start();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        watcher.Dispose();

        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    private sealed class TimeoutThenSessionsHandler : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref _requestCount);
            if (count == 1)
            {
                throw new TaskCanceledException("simulated request timeout");
            }

            return Task.FromResult(count == 2
                ? SessionsJson(("s1", "任务一", Running: true))
                : SessionsJson(("s1", "任务一", Running: false)));
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellation test unexpectedly resumed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
