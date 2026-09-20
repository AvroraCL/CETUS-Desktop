using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cetus.Hosting;

public sealed record DshMuxItem(string StreamId, JsonElement Value);

public sealed record DshMuxStreamError(string StreamId, string Code, string Message);

/// <summary>
/// Minimal client for DSH's multiplexed stream WebSocket
/// (<c>/api/remote.mux</c>, DSH ≥ 0.1.6). Frames are JSON text on both
/// directions: the client sends <c>open</c>/<c>cancel</c>, the server
/// answers <c>item</c>/<c>end</c>/<c>error</c>. WebSocket ping frames are
/// answered automatically by <see cref="ClientWebSocket"/>. Reconnection is
/// the caller's policy — this class only reports <see cref="Disconnected"/>.
/// See docs/dev/dsh-mux-protocol.md for the wire details.
/// </summary>
public sealed class DshStreamMuxClient : IDisposable
{
    private readonly string? _dshHomeOverride;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCancellation;
    private Task? _receiveLoop;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _disposeGate = new();
    private bool _disposed;

    public DshStreamMuxClient(string? dshHomeOverride = null)
    {
        _dshHomeOverride = dshHomeOverride;
    }

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>Raised on the receive loop thread for every stream item.</summary>
    public event Action<DshMuxItem>? Item;

    /// <summary>Raised when a stream finishes normally.</summary>
    public event Action<string>? StreamEnded;

    /// <summary>Raised when the server reports a stream failure.</summary>
    public event Action<DshMuxStreamError>? StreamFailed;

    /// <summary>Raised when the socket closes for any reason (after the loop drains).</summary>
    public event Action? Disconnected;

    public static Uri BuildMuxUri(Uri origin) => new($"ws://{origin.Authority}/api/remote.mux");

    public async Task ConnectAsync(Uri origin, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null)
        {
            throw new InvalidOperationException("DshStreamMuxClient 已连接。");
        }

        var socket = new ClientWebSocket();
        if (DshAuth.TryGetSessionCookie(origin, _dshHomeOverride) is { } cookie)
        {
            socket.Options.SetRequestHeader("Cookie", $"{cookie.Name}={cookie.Value}");
        }

        await socket.ConnectAsync(BuildMuxUri(origin), cancellationToken);
        _socket = socket;
        _loopCancellation = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _loopCancellation.Token));
    }

    public async Task OpenStreamAsync(string streamId, string endpoint, JsonElement payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SendAsync(
            JsonSerializer.Serialize(new { type = "open", streamId, endpoint, payload }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
            cancellationToken);
    }

    public Task CancelStreamAsync(string streamId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SendAsync(
            JsonSerializer.Serialize(new { type = "cancel", streamId }),
            cancellationToken);
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("DshStreamMuxClient 未连接。");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = new ArrayBufferWriter<byte>();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                DispatchFrame(JsonSerializer.Deserialize<JsonElement>(message.WrittenSpan.ToArray()));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (JsonException)
        {
            // A malformed frame ends the connection the same way the server
            // treats one of ours: drop the socket instead of guessing.
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    private void DispatchFrame(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object
            || !frame.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !frame.TryGetProperty("streamId", out JsonElement streamElement)
            || streamElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string streamId = streamElement.GetString() ?? string.Empty;
        string type = typeElement.GetString() ?? string.Empty;
        switch (type)
        {
            case "item" when frame.TryGetProperty("value", out JsonElement value):
                Item?.Invoke(new DshMuxItem(streamId, value.Clone()));
                break;
            case "end":
                StreamEnded?.Invoke(streamId);
                break;
            case "error" when frame.TryGetProperty("error", out JsonElement error):
                string code = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out JsonElement codeElement)
                    && codeElement.ValueKind == JsonValueKind.String
                        ? codeElement.GetString() ?? "unknown"
                        : "unknown";
                string messageText = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement messageElement)
                    && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString() ?? string.Empty
                        : string.Empty;
                StreamFailed?.Invoke(new DshMuxStreamError(streamId, code, messageText));
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loopCancellation?.Cancel();
        try
        {
            _socket?.Dispose();
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException)
        {
        }

        try
        {
            _receiveLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _loopCancellation?.Dispose();
    }
}
