using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Cetus.DshStatus;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshSessionClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4301/");

    [Fact]
    public async Task GetSessionsAsync_UsesSlashEndpointAndUnderscoreRequestWrapper()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse("""{ "result": { "ok": true, "value": { "items": [] } } }"""));
        using var client = new DshSessionClient(handler: handler);

        await client.GetSessionsAsync(Endpoint, CancellationToken.None);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/session/list", request.Uri!.AbsolutePath);

        using JsonDocument document = JsonDocument.Parse(request.Body);
        JsonElement root = document.RootElement;
        Assert.Equal("client-request", root.GetProperty("type").GetString());
        Assert.Equal("session/list", root.GetProperty("method").GetString());
        Assert.True(root.TryGetProperty("payload", out JsonElement payload));

        // DSH's typert gateway rejects an empty args object for session/list
        // with gateway/arguments-invalid ("missing \"_request\""), which made
        // every poll fail and silently disabled agent-finished notifications.
        JsonElement args = payload.GetProperty("args");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.True(args.TryGetProperty("_request", out JsonElement requestArg));
        Assert.Equal(JsonValueKind.Object, requestArg.ValueKind);
        Assert.Empty(requestArg.EnumerateObject());
    }

    [Fact]
    public async Task GetSessionsAsync_ParsesProjectedTitlesAndCwdFallback()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse("""
            {
              "result": {
                "ok": true,
                "value": {
                  "items": [
                    {
                      "sessionId": "s1",
                      "cwd": "F:\\repos\\demo",
                      "running": true,
                      "updatedAt": 1700000000000,
                      "projections": { "values": { "title": "重构登录页" } }
                    },
                    {
                      "sessionId": "s2",
                      "cwd": "F:\\apps\\tool",
                      "running": false,
                      "updatedAt": "2026-01-02T03:04:05Z"
                    },
                    { "cwd": "no-session-id", "running": true },
                    "not-an-object"
                  ]
                }
              }
            }
            """));
        using var client = new DshSessionClient(handler: handler);

        IReadOnlyList<DshSessionInfo> sessions = await client.GetSessionsAsync(Endpoint, CancellationToken.None);

        Assert.Equal(2, sessions.Count);

        Assert.Equal("s1", sessions[0].SessionId);
        Assert.Equal("重构登录页", sessions[0].Title);
        Assert.True(sessions[0].Running);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            sessions[0].UpdatedAt);

        Assert.Equal("s2", sessions[1].SessionId);
        Assert.Equal("tool", sessions[1].Title);
        Assert.False(sessions[1].Running);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), sessions[1].UpdatedAt);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_PostsRequestWrappedPathAndReturnsId()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse("""
            { "result": { "ok": true, "value": { "workspace": { "workspaceId": "ws-1", "path": "F:\\repos\\demo" }, "created": true } } }
            """));
        using var client = new DshSessionClient(handler: handler);

        string workspaceId = await client.CreateWorkspaceAsync(Endpoint, @"F:\repos\demo", CancellationToken.None);

        Assert.Equal("ws-1", workspaceId);
        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/workspace/create", request.Uri!.AbsolutePath);
        Assert.Contains(@"""request"":{""path"":""F:\\repos\\demo""}", request.Body);
    }

    [Fact]
    public async Task CreateSessionAsync_PostsWorkspaceIdAndReturnsSessionId()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse(
            """{ "result": { "ok": true, "value": { "sessionId": "session-42" } } }"""));
        using var client = new DshSessionClient(handler: handler);

        string sessionId = await client.CreateSessionAsync(Endpoint, "ws-1", CancellationToken.None);

        Assert.Equal("session-42", sessionId);
        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/session/create", request.Uri!.AbsolutePath);
        Assert.Contains("\"workspaceId\":\"ws-1\"", request.Body);
    }

    [Fact]
    public async Task GetSessionsAsync_SendsSessionCookieFromDshHome()
    {
        using var directory = new TemporaryDirectory();
        string secret = DshAuth.Base64Url(new byte[32]);
        File.WriteAllText(
            Path.Combine(directory.Path, ".credentials.yaml"),
            $"""
            version: 1
            records:
              client-connection/browser-session:
                kind: grant
                payload:
                  version: 1
                  secret: {secret}

            """);

        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse("""{ "result": { "ok": true, "value": { "items": [] } } }"""));
        using var client = new DshSessionClient(directory.Path, handler);

        await client.GetSessionsAsync(Endpoint, CancellationToken.None);

        Assert.Single(handler.Requests);
        string? cookie = handler.Requests[0].Cookie;
        Assert.NotNull(cookie);
        Assert.StartsWith("dsh-auth-", cookie, StringComparison.Ordinal);
        Assert.Contains("=v1.", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSessionsAsync_WithoutSecret_OmitsCookie()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse("""{ "result": { "ok": true, "value": { "items": [] } } }"""));
        using var client = new DshSessionClient(
            Path.Combine(Path.GetTempPath(), "cetus-no-such-dsh-home"), handler);

        await client.GetSessionsAsync(Endpoint, CancellationToken.None);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Null(request.Cookie);
    }

    [Fact]
    public async Task GetSessionsAsync_FailedResult_Throws()
    {
        FakeDshHandler handler = new();
        handler.Responses.Enqueue(JsonResponse("""{ "result": { "ok": false, "error": "boom" } }"""));
        using var client = new DshSessionClient(handler: handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSessionsAsync(Endpoint, CancellationToken.None));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string Body, string? Cookie);

    private sealed class FakeDshHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public Queue<HttpResponseMessage> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // HttpClient disposes the content after sending, so the body and
            // headers must be captured here, while the request is alive.
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            string? cookie = request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values)
                ? values!.First()
                : null;
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body, cookie));
            return Responses.Count > 0
                ? Responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestWorkspace.CreateDirectory();
        }

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
