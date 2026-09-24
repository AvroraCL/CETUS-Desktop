using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using Cetus.Hosting;

namespace Cetus.DshStatus;

public sealed record DshTurnEndedEventArgs(string SessionId, string Title, string ReasonKind);

/// <summary>
/// Realtime "turn finished" detection on top of the mux protocol: subscribes
/// a <c>session/follow</c> stream for the most recently active session and
/// raises <see cref="TurnEnded"/> the moment a turn closes — zero latency
/// compared to the polling fallback in <see cref="DshSessionWatcher"/>. The
/// stream is re-opened periodically so a newer session takes over, and all
/// failures degrade silently to the reconnect cycle.
/// </summary>
public sealed class DshTurnEndWatcher : IDisposable
{
    private static readonly TimeSpan SessionDiscoveryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FollowHoldInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly DshSessionClient _sessions;
    private readonly Func<Uri> _endpoint;
    private readonly string? _dshHomeOverride;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _disposed;

    public DshTurnEndWatcher(
        DshSessionClient sessions,
        Func<Uri> endpoint,
        string? dshHomeOverride = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sessions = sessions;
        _endpoint = endpoint;
        _dshHomeOverride = dshHomeOverride;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>Raised on the receive loop thread after a turn closes.</summary>
    public event EventHandler<DshTurnEndedEventArgs>? TurnEnded;

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cancellation.Token));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? latest = await DiscoverLatestSessionAsync(token);
            if (latest is null)
            {
                await SleepAsync(SessionDiscoveryInterval, token);
                continue;
            }

            try
            {
                await FollowAsync(latest, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException
                or InvalidOperationException
                or WebSocketException
                or JsonException)
            {
                Configuration.RuntimeLog.Append(
                    "DshTurnEndWatcher follow failed: " + error.Message);
            }

            await SleepAsync(ReconnectDelay, token);
        }
    }

    /// <summary>Returns the id of the most recently updated session, or null when DSH has none or is unreachable.</summary>
    private async Task<string?> DiscoverLatestSessionAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<DshSessionInfo> sessions = await _sessions.GetSessionsAsync(_endpoint(), token);
                DateTimeOffset newest = DateTimeOffset.MinValue;
                string? latest = null;
                string? latestTitle = null;
                foreach (DshSessionInfo session in sessions)
                {
                    if (session.UpdatedAt > newest)
                    {
                        newest = session.UpdatedAt;
                        latest = session.SessionId;
                        latestTitle = session.Title;
                    }
                }

                _latestTitle = latestTitle;
                return latest;
            }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
            {
                await SleepAsync(ReconnectDelay, token);
            }
        }

        return null;
    }

    private string? _latestTitle;

    private async Task FollowAsync(string sessionId, CancellationToken token)
    {
        using var mux = new DshStreamMuxClient(_dshHomeOverride);
        mux.Item += item =>
        {
            if (DshFollowFrames.TryParseEvent(item.Value) is { } parsed
                && parsed.EventType == "turn/end"
                && parsed.TurnEndKind is { } reason)
            {
                TurnEnded?.Invoke(this, new DshTurnEndedEventArgs(sessionId, _latestTitle ?? sessionId, reason));
            }
        };

        await mux.ConnectAsync(_endpoint(), token);
        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            args = new
            {
                request = new
                {
                    address = new { kind = "session", sessionId },
                },
            },
        }));
        await mux.OpenStreamAsync(sessionId, "session/follow", payload.RootElement, token);

        // Hold the stream for a discovery cycle so a newer session (or a
        // restarted DSH) takes over promptly; in-flight items are dispatched
        // by the Item handler during this window.
        await SleepAsync(FollowHoldInterval, token);
    }

    private Task SleepAsync(TimeSpan delay, CancellationToken token) => _delay(delay, token);

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

        cancellation?.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The loop unwinds through cancellation; nothing to observe.
        }

        cancellation?.Dispose();
    }
}
