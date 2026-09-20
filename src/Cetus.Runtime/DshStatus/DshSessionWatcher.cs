using System.Text.Json;
using Cetus.Configuration;

namespace Cetus.DshStatus;

public sealed record DshAgentFinishedEventArgs(string SessionId, string Title);

/// <summary>
/// Polls the DSH session list on an interval and raises <see cref="AgentFinished"/>
/// when a session's agent loop transitions from running to idle. The first poll
/// only establishes a baseline; each completion is reported at most once per
/// cooldown window so flickering sessions cannot spam the tray.
/// Does not own <paramref name="client"/> — the caller keeps disposal.
/// </summary>
public sealed class DshSessionWatcher : IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultCompletionCooldown = TimeSpan.FromMinutes(2);

    private readonly DshSessionClient _client;
    private readonly Func<Uri> _endpoint;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _completionCooldown;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private Dictionary<string, bool> _runningBySession = new(StringComparer.Ordinal);
    private Dictionary<string, DateTimeOffset> _notifiedAt = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _hasBaseline;
    private bool _disposed;
    private int _consecutiveFailures;

    public DshSessionWatcher(
        DshSessionClient client,
        Func<Uri> endpoint,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        TimeSpan? completionCooldown = null,
        Action<string>? log = null)
    {
        _client = client;
        _endpoint = endpoint;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _completionCooldown = completionCooldown ?? DefaultCompletionCooldown;
        _log = log ?? RuntimeLog.Append;
    }

    /// <summary>Raised on the poll loop thread after each completion is detected.</summary>
    public event EventHandler<DshAgentFinishedEventArgs>? AgentFinished;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _loop = Task.Run(() => PollLoopAsync(_cancellation.Token));
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(_pollInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await PollOnceAsync(token);
                    if (_consecutiveFailures > 0)
                    {
                        _log($"DSH session polling recovered after {_consecutiveFailures} failure(s).");
                        _consecutiveFailures = 0;
                    }
                }
                catch (IOException error)
                {
                    RecordTransientFailure(error);
                }
                catch (HttpRequestException error)
                {
                    RecordTransientFailure(error);
                }
                catch (InvalidOperationException error)
                {
                    RecordTransientFailure(error);
                }
                catch (JsonException error)
                {
                    RecordTransientFailure(error);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException error)
                {
                    // HttpClient reports its own request timeout as cancellation.
                    // Preserve the last state and retry on the next tick.
                    RecordTransientFailure(error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Dispose.
        }
    }

    private void RecordTransientFailure(Exception error)
    {
        int count = ++_consecutiveFailures;
        if (count == 1 || count % 6 == 0)
        {
            _log($"DSH session polling failed ({count} consecutive): {error.Message}");
        }
    }

    internal async Task PollOnceAsync(CancellationToken token)
    {
        IReadOnlyList<DshSessionInfo> sessions = await _client.GetSessionsAsync(_endpoint(), token);

        List<DshAgentFinishedEventArgs>? finished = null;
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            var current = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (DshSessionInfo session in sessions)
            {
                current[session.SessionId] = session.Running;
            }

            if (_hasBaseline)
            {
                foreach (DshSessionInfo session in sessions)
                {
                    bool wasRunning = _runningBySession.TryGetValue(session.SessionId, out bool running)
                        && running;
                    bool cooledDown = !_notifiedAt.TryGetValue(session.SessionId, out DateTimeOffset notifiedAt)
                        || now - notifiedAt >= _completionCooldown;
                    if (!session.Running && wasRunning && cooledDown)
                    {
                        _notifiedAt[session.SessionId] = now;
                        (finished ??= []).Add(new DshAgentFinishedEventArgs(session.SessionId, session.Title));
                    }
                }

                PruneCooldowns(now);
            }

            _hasBaseline = true;
            _runningBySession = current;
        }

        if (finished is not null)
        {
            foreach (DshAgentFinishedEventArgs args in finished)
            {
                AgentFinished?.Invoke(this, args);
            }
        }
    }

    private void PruneCooldowns(DateTimeOffset now)
    {
        if (_notifiedAt.Count <= 64)
        {
            return;
        }

        _notifiedAt = _notifiedAt
            .Where(entry => now - entry.Value < _completionCooldown)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            loop = _loop;
            _cancellation = null;
            _loop = null;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        // Brief, best-effort join so the poll is not left running mid-request;
        // a hung request is abandoned after the wait and dies with the token.
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Poll failures and cancellation races are expected here.
        }

        if (cancellation is not null)
        {
            cancellation.Dispose();
        }
    }
}
