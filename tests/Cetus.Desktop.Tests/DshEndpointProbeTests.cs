using System.Net;
using System.Net.Sockets;
using System.Text;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// The health probe must never turn "the host is alive but refused my cookie"
/// into "the host is dead": Cetus kills the host on a health failure, which
/// would destroy the running agent turn instead of repairing anything.
/// </summary>
public sealed class DshEndpointProbeTests
{
    [Fact]
    public async Task Probe_ReturnsHealthyWhenRootMarkerPresent()
    {
        using var probe = CreateProbe(HttpStatusCode.OK, "<html><div id=\"root\"></div></html>");

        DshProbeResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DshProbeStatus.Healthy, result.Status);
        Assert.True(result.IsHealthy);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Probe_ReportsAuthRejectionSeparatelyFromUnhealthy(HttpStatusCode status)
    {
        using var probe = CreateProbe(status, "unauthorized");

        DshProbeResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DshProbeStatus.AuthRejected, result.Status);
        Assert.False(result.IsHealthy);
        Assert.Contains(((int)status).ToString(), result.Detail);
    }

    [Fact]
    public async Task Probe_ReturnsUnhealthyWhenMarkerMissing()
    {
        using var probe = CreateProbe(HttpStatusCode.OK, "<html><div id=\"not-root\"></div></html>");

        DshProbeResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DshProbeStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task Probe_ReturnsUnhealthyWithDetailOnTransportFailure()
    {
        using var probe = new DshEndpointProbe(
            new Uri("http://127.0.0.1:9/"),
            dshHomeOverride: null,
            timeout: TimeSpan.FromSeconds(1),
            handler: new ThrowingHandler());

        DshProbeResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DshProbeStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Detail);
    }

    /// <summary>
    /// A configured proxy must not swallow the loopback probe: WebView2 bypasses
    /// proxies for 127.0.0.1 implicitly, so a probe that did not would report a
    /// healthy host as dead and get it killed.
    /// </summary>
    [Fact]
    public async Task Probe_LoopbackProbeIsNotSwallowedByAConfiguredProxy()
    {
        int port = FreePort();
        using var server = new LoopbackMarkupServer(port);
        string? originalUpper = Environment.GetEnvironmentVariable("HTTP_PROXY");
        string? originalLower = Environment.GetEnvironmentVariable("http_proxy");
        try
        {
            // A dead proxy address: honouring it for loopback would fail the probe.
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:1");
            Environment.SetEnvironmentVariable("http_proxy", "http://127.0.0.1:1");

            using var probe = new DshEndpointProbe(
                new Uri($"http://127.0.0.1:{port}/"),
                dshHomeOverride: null,
                timeout: TimeSpan.FromSeconds(5));

            DshProbeResult result = await probe.ProbeAsync(CancellationToken.None);

            Assert.True(
                result.Status == DshProbeStatus.Healthy,
                $"probe status={result.Status}, detail={result.Detail}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", originalUpper);
            Environment.SetEnvironmentVariable("http_proxy", originalLower);
        }
    }

    /// <summary>Serves the Harness root marker over loopback for one probe.</summary>
    private sealed class LoopbackMarkupServer : IDisposable
    {
        private const string Body = "<html><div id=\"root\"></div></html>";

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serveTask;

        public LoopbackMarkupServer(int port)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _serveTask = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    byte[] body = Encoding.UTF8.GetBytes(Body);
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Close();
            try
            {
                _serveTask.GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
            }
            _cancellation.Dispose();
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static DshEndpointProbe CreateProbe(HttpStatusCode status, string body) =>
        new(
            new Uri("http://127.0.0.1:3080/"),
            dshHomeOverride: null,
            timeout: TimeSpan.FromSeconds(2),
            handler: new StaticHandler(status, body));

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
