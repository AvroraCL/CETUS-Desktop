using System.Net;
using System.Net.Http;
using System.Text;
using Cetus.DshStatus;
using Xunit;

namespace Cetus.Desktop.Tests;

public sealed class DshStatusClientTests
{
    private const string WorkspaceListJson = """
        {"type":"server-response","rpcId":"r1","result":{"ok":true,"value":{"items":[
            {"workspaceId":"a","path":"F:\\A","title":"Alpha","sessionIds":["s1","s2"],"updatedAt":"2026-08-28T10:00:00Z"},
            {"workspaceId":"b","path":"F:\\B","title":"Beta","sessionIds":["s3"],"updatedAt":"2026-08-28T12:00:00Z"}
        ]}}}
        """;

    private const string SessionListJson = """
        {"type":"server-response","rpcId":"r2","result":{"ok":true,"value":{"items":[
            {"sessionId":"s1","projections":{"values":{
                "tokenUsage":{"uncachedInputTokens":100,"outputTokens":50,"cacheReadTokens":10,"cacheWriteTokens":5},
                "sessionStats":{"turns":2,"steps":7}}}},
            {"sessionId":"s2","projections":{"values":{
                "tokenUsage":{"uncachedInputTokens":30,"outputTokens":20,"cacheReadTokens":0,"cacheWriteTokens":0},
                "sessionStats":{"turns":1,"steps":3}}}},
            {"sessionId":"s3","projections":{"values":{"title":"empty"}}}
        ]}}}
        """;

    private const string DescribeJson = """
        {"type":"server-response","rpcId":"r3","result":{"ok":true,"value":{
            "version":"0.0.1","cwd":"F:\\Cetus","provider":"deepseek-official","model":"deepseek-v4-flash"}}}
        """;

    private const string BalanceJson = """
        {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.50","granted_balance":"10.00","topped_up_balance":"100.50"}]}
        """;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string? Url, string? Method, string? Body)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add((request.RequestUri?.ToString(), request.Method.Method, body));
            return Task.FromResult(Responder(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GetStatusAsync_SumsUsageAcrossSessionsAndPicksLatestWorkspace()
    {
        var handler = new FakeHandler
        {
            Responder = request => Json(request.RequestUri!.PathAndQuery.Contains("workspace")
                ? WorkspaceListJson
                : request.RequestUri.PathAndQuery.Contains("session") ? SessionListJson : DescribeJson),
        };
        using var client = new DshStatusClient(handler);

        DshStatusSnapshot snapshot = await client.GetStatusAsync(
            new Uri("http://127.0.0.1:3080/"),
            CancellationToken.None);

        Assert.Equal("Beta", snapshot.Workspace!.Title);
        Assert.Equal("F:\\B", snapshot.Workspace.Path);
        Assert.Equal(1, snapshot.Workspace.SessionCount);
        Assert.Equal("deepseek-official", snapshot.Provider);
        Assert.Equal("deepseek-v4-flash", snapshot.Model);
        Assert.Equal(130, snapshot.Usage.InputTokens);
        Assert.Equal(70, snapshot.Usage.OutputTokens);
        Assert.Equal(10, snapshot.Usage.CacheReadTokens);
        Assert.Equal(3, snapshot.Usage.SessionCount);
        Assert.Equal(3, snapshot.Usage.Turns);
        Assert.Equal(10, snapshot.Usage.Steps);
        Assert.Equal(2, snapshot.Usage.Sessions.Count);
        Assert.Equal(50, snapshot.Usage.Sessions[0].OutputTokens);
        Assert.Equal(20, snapshot.Usage.Sessions[1].OutputTokens);
    }

    [Fact]
    public async Task GetStatusAsync_SendsRpcEnvelopeAndSurfacesErrors()
    {
        const string errorJson = """
            {"type":"server-response","rpcId":"x","result":{"ok":false,"error":{"code":"bad-request","message":"boom"}}}
            """;
        var handler = new FakeHandler
        {
            Responder = request => Json(
                request.RequestUri!.PathAndQuery.Contains("workspace")
                    ? WorkspaceListJson
                    : errorJson),
        };
        using var client = new DshStatusClient(handler);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetStatusAsync(new Uri("http://127.0.0.1:3080/"), CancellationToken.None));

        Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
        var (url, method, body) = handler.Requests[0];
        Assert.Equal("http://127.0.0.1:3080/api/workspace.list", url);
        Assert.Equal("POST", method);
        Assert.Contains("\"type\":\"client-request\"", body);
        Assert.Contains("\"method\":\"workspace.list\"", body);
    }

    [Fact]
    public async Task GetBalanceAsync_ParsesPlatformResponse()
    {
        var handler = new FakeHandler
        {
            Responder = request => Json(BalanceJson),
        };
        using var client = new DshStatusClient(handler);

        DeepSeekBalance? balance = await client.GetBalanceAsync("sk-test", "https://api.deepseek.com", CancellationToken.None);

        Assert.NotNull(balance);
        Assert.True(balance!.IsAvailable);
        Assert.Equal("CNY", balance.Currency);
        Assert.Equal(110.50m, balance.TotalBalance);
        Assert.Equal(10.00m, balance.GrantedBalance);
        Assert.Equal("https://api.deepseek.com/user/balance", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetBalanceAsync_ReturnsNullOnHttpErrors()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        using var client = new DshStatusClient(handler);

        Assert.Null(await client.GetBalanceAsync("sk-bad", "https://api.deepseek.com", CancellationToken.None));
    }
}

public sealed class DshCredentialsTests
{
    [Fact]
    public void ReadApiKey_PrefersEnvironmentOverFile()
    {
        string? originalKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        using var directory = new TemporaryDirectory();
        string yaml = Path.Combine(directory.Path, ".credentials.yaml");
        File.WriteAllText(yaml, $"DEEPSEEK_API_KEY: \"sk-from-file\"\nOTHER: 1\n");
        try
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-from-env");

            Assert.Equal("sk-from-env", DshCredentials.ReadApiKey(directory.Path));

            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            Assert.Equal("sk-from-file", DshCredentials.ReadApiKey(directory.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", originalKey);
        }
    }

    [Fact]
    public void ReadApiKey_ReturnsNullWhenMissingOrEmpty()
    {
        string? originalKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        using var directory = new TemporaryDirectory();
        try
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            Assert.Null(DshCredentials.ReadApiKey(directory.Path));

            string yaml = Path.Combine(directory.Path, ".credentials.yaml");
            File.WriteAllText(yaml, "OTHER_REF: value\nDEEPSEEK_API_KEY:\n");
            Assert.Null(DshCredentials.ReadApiKey(directory.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", originalKey);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = TestWorkspace.CreateDirectory();

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
