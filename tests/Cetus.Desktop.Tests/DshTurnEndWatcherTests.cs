using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cetus.DshStatus;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// End-to-end coverage for DshTurnEndWatcher: an HttpListener stands in for
/// DSH (session/list over POST, the mux upgrade over WebSocket) and the
/// watcher must surface the turn/end reason in realtime.
/// </summary>
public sealed class DshTurnEndWatcherTests : IAsyncLifetime
{
    private readonly HttpListener _listener = new();
    private int _port;

    public async Task InitializeAsync()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int candidate = Cetus.Hosting.FreePortFinder.Reserve();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                _listener.Start();
                _port = candidate;
                return;
            }
            catch (HttpListenerException)
            {
            }
        }

        throw new InvalidOperationException("No free HttpListener port was available.");
    }

    public Task DisposeAsync()
    {
        _listener.Close();
        return Task.CompletedTask;
    }

    private Uri Origin => new($"http://127.0.0.1:{_port}/");

    private static async Task<WebSocket> AcceptAsync(HttpListenerContext context)
    {
        HttpListenerWebSocketContext websocketContext = await context.AcceptWebSocketAsync(null);
        return websocketContext.WebSocket;
    }

    private static async Task SendJson(WebSocket socket, object frame)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonElement> ReceiveJson(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var received = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            received.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(received.ToArray()));
    }

    [Fact]
    public async Task Start_SubscribesTheLatestSession_AndRaisesTurnEnded()
    {
        string sessionListJson = """
            { "result": { "ok": true, "value": { "items": [
                { "sessionId": "s-latest", "cwd": "F:\\repos\\demo", "running": true, "updatedAt": 1758400000000,
                  "projections": { "values": { "title": "最新会话" } } }
            ] } } }
            """;
        TaskCompletionSource<JsonElement> openReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _listener.Start();

        Task serverTask = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                if (context.Request.HttpMethod == "POST"
                    && context.Request.Url!.AbsolutePath == "/api/session/list")
                {
                    string body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                    string items = body.Contains('"')
                        ? body
                        : string.Empty;
                    byte[] responseBytes = Encoding.UTF8.GetBytes(
                        """{ "result": { "ok": true, "value": { "items": [ { "sessionId": "s-latest", "cwd": "F:\\repos\\demo", "running": true, "updatedAt": 1758400000000 } ] } } }""");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes);
                    context.Response.Close();
                    continue;
                }

                if (context.Request.Headers["Upgrade"] == "websocket"
                    && context.Request.Url!.AbsolutePath == "/api/remote.mux")
                {
                    using WebSocket server = await AcceptAsync(context);
                    await SendJson(server, JsonSerializer.Deserialize<JsonElement>("""
                        { "type": "item", "streamId": "s-latest", "value": { "type": "event", "event": { "type": "turn/end", "seq": 7, "data": { "reason": { "kind": "completed" } } } } }
                        """));
                    // Served the one frame this test needs; stop dispatching so
                    // the test's own WaitAsync cannot time out on the loop.
                    return;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        });

        TaskCompletionSource<DshTurnEndedEventArgs> turned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new DshTurnEndWatcher(
            new DshSessionClient(),
            () => Origin,
            delay: static (_, _) => Task.CompletedTask);
        watcher.TurnEnded += (_, e) => turned.TrySetResult(e);
        watcher.Start();

        try
        {
            DshTurnEndedEventArgs args = await turned.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("s-latest", args.SessionId);
            Assert.Equal("completed", args.ReasonKind);
        }
        catch (TimeoutException)
        {
            throw;
        }
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
