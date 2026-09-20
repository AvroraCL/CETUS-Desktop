using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace Cetus.Hosting;

/// <summary>
/// Outcome of one semantic health probe. <see cref="AuthRejected"/> exists
/// because DSH requires a browser-session cookie: a 401/403 means the host is
/// alive and answering, and killing it would destroy live work instead of
/// repairing anything.
/// </summary>
public enum DshProbeStatus
{
    /// <summary>HTTP 200 and the Harness shell root marker was present.</summary>
    Healthy,

    /// <summary>Reachable, but the session cookie was refused (401/403).</summary>
    AuthRejected,

    /// <summary>Wrong status code, missing marker, or HTTP-level failure.</summary>
    Unhealthy,
}

public readonly record struct DshProbeResult(DshProbeStatus Status, string? Detail)
{
    public bool IsHealthy => Status == DshProbeStatus.Healthy;
}

/// <summary>
/// Owns all observations of the configured DSH endpoint: semantic HTTP health
/// and the lower-level loopback port occupancy check.
/// </summary>
internal sealed class DshEndpointProbe : IDisposable
{
    private readonly Uri _endpoint;
    private readonly string? _dshHomeOverride;
    private readonly HttpClient _client;

    public DshEndpointProbe(
        Uri endpoint,
        string? dshHomeOverride = null,
        TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint;
        _dshHomeOverride = dshHomeOverride;

        // This probe only ever talks to the local DSH host, so it must never be
        // sent to a proxy. HttpClient honours HTTP_PROXY/http_proxy and the
        // WinINET settings, and a proxy (Clash, corporate MITM) that is merely
        // slow or unreachable for loopback would make a perfectly healthy host
        // look dead — which Cetus answers by killing it.
        HttpClient client;
        if (handler is not null)
        {
            client = new HttpClient(handler);
        }
        else
        {
            client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        }

        client.Timeout = timeout ?? TimeSpan.FromSeconds(5);
        _client = client;
    }

    public bool IsPortInUse() =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(candidate => candidate.Port == _endpoint.Port);

    public async Task<DshProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            if (DshAuth.TryGetSessionCookie(_endpoint, _dshHomeOverride) is { } cookie)
            {
                request.Headers.Add("Cookie", $"{cookie.Name}={cookie.Value}");
            }

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new DshProbeResult(
                    DshProbeStatus.AuthRejected,
                    $"HTTP {(int)response.StatusCode}");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new DshProbeResult(
                    DshProbeStatus.Unhealthy,
                    $"HTTP {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains("id=\"root\"", StringComparison.Ordinal)
                ? new DshProbeResult(DshProbeStatus.Healthy, null)
                : new DshProbeResult(DshProbeStatus.Unhealthy, "缺少 id=\"root\" 标记");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new DshProbeResult(DshProbeStatus.Unhealthy, DescribeFailure(error));
        }
    }

    private static string DescribeFailure(Exception error) => error switch
    {
        TaskCanceledException or OperationCanceledException => "请求超时",
        HttpRequestException { InnerException: { } inner } => inner.Message,
        _ => error.Message,
    };

    public void Dispose() => _client.Dispose();
}
