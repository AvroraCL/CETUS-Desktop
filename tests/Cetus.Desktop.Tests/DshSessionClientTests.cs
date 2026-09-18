using System.Net;
using System.Net.Http;
using System.Text;
using Cetus.DshStatus;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshSessionClientTests
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4301/");

    [Fact]
    public async Task GetSessionsAsync_ParsesItemsAndProjectedTitles()
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
                      "title": { "title": "重构登录页" }
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
        HttpRequestMessage request = handler.Requests[0];
        Assert.Equal("/api/session.list", request.RequestUri!.AbsolutePath);
        Assert.True(request.Headers.TryGetValues("Cookie", out IEnumerable<string>? cookies));
        string cookie = Assert.Single(cookies!);
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

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.Contains("Cookie"));
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

    private sealed class FakeDshHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Queue<HttpResponseMessage> Responses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(
                Responses.Count > 0
                    ? Responses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
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
