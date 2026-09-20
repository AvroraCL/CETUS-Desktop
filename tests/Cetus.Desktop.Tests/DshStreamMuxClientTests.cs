using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// Real-socket coverage for the mux client: an HttpListener accepts the
/// WebSocket upgrade and plays the server side of /api/remote.mux.
/// </summary>
public sealed class DshStreamMuxClientTests : IAsyncLifetime
{
    private readonly HttpListener _listener = new();
    private int _port;

    public async Task InitializeAsync()
    {
        // HttpListener cannot bind port 0; probe for a free one first.
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
    public async Task Connect_OpenStream_ReceivesItemsThenEnd()
    {
        string? cookieHeader = null;
        JsonElement? openFrame = null;
        _listener.Start();

        Task serverTask = Task.Run(async () =>
        {
            HttpListenerContext context = await _listener.GetContextAsync();
            cookieHeader = context.Request.Headers["Cookie"];
            using WebSocket server = await AcceptAsync(context);

            openFrame = await ReceiveJson(server);
            await SendJson(server, new
            {
                type = "item",
                streamId = "mux-1",
                value = new { running = true },
            });
            await SendJson(server, new { type = "end", streamId = "mux-1" });
        });

        TaskCompletionSource<DshMuxItem> item = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new DshStreamMuxClient();
        client.Item += i => item.TrySetResult(i);
        client.StreamEnded += s => ended.TrySetResult(s);
        await client.ConnectAsync(Origin, CancellationToken.None);
        await client.OpenStreamAsync(
            "mux-1", "session/follow",
            JsonSerializer.Deserialize<JsonElement>("{\"args\":{}}"),
            CancellationToken.None);

        DshMuxItem received = await item.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("mux-1", received.StreamId);
        Assert.True(received.Value.GetProperty("running").GetBoolean());
        Assert.Equal("mux-1", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(cookieHeader);
        Assert.StartsWith("dsh-auth-", cookieHeader!, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"open\"", openFrame!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_SendsCancelFrame()
    {
        TaskCompletionSource<JsonElement> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _listener.Start();
        Task serverTask = Task.Run(async () =>
        {
            HttpListenerContext context = await _listener.GetContextAsync();
            using WebSocket server = await AcceptAsync(context);
            await ReceiveJson(server); // drain the open frame
            await SendJson(server, new { type = "item", streamId = "mux-2", value = 1 });
            received.TrySetResult(await ReceiveJson(server));
        });

        using var client = new DshStreamMuxClient();
        await client.ConnectAsync(Origin, CancellationToken.None);
        await client.OpenStreamAsync(
            "mux-2", "session/follow",
            JsonSerializer.Deserialize<JsonElement>("{\"args\":{}}"),
            CancellationToken.None);
        await client.CancelStreamAsync("mux-2", CancellationToken.None);

        JsonElement frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancel", frame.GetProperty("type").GetString());
        Assert.Equal("mux-2", frame.GetProperty("streamId").GetString());
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ErrorFrame_RaisesStreamFailed()
    {
        _listener.Start();
        Task serverTask = Task.Run(async () =>
        {
            HttpListenerContext context = await _listener.GetContextAsync();
            using WebSocket server = await AcceptAsync(context);
            await ReceiveJson(server); // open
            await SendJson(server, new
            {
                type = "error",
                streamId = "mux-3",
                error = new { code = "session/unknown", message = "no such session" },
            });
        });

        TaskCompletionSource<DshMuxStreamError> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new DshStreamMuxClient();
        client.StreamFailed += e => failed.TrySetResult(e);
        await client.ConnectAsync(Origin, CancellationToken.None);
        await client.OpenStreamAsync(
            "mux-3", "session/follow",
            JsonSerializer.Deserialize<JsonElement>("{\"args\":{}}"),
            CancellationToken.None);

        DshMuxStreamError error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("session/unknown", error.Code);
        Assert.Equal("no such session", error.Message);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
