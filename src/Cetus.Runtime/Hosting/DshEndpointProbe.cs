using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;

namespace Cetus.Hosting;

/// <summary>
/// Owns all observations of the configured DSH endpoint: semantic HTTP health
/// and the lower-level loopback port occupancy check.
/// </summary>
internal sealed class DshEndpointProbe : IDisposable
{
    private readonly Uri _endpoint;
    private readonly string? _dshHomeOverride;
    private readonly HttpClient _client;

    public DshEndpointProbe(Uri endpoint, string? dshHomeOverride = null)
    {
        _endpoint = endpoint;
        _dshHomeOverride = dshHomeOverride;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public bool IsPortInUse() =>
        IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(candidate => candidate.Port == _endpoint.Port);

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            if (DshAuth.TryGetSessionCookie(_endpoint, _dshHomeOverride) is { } cookie)
            {
                request.Headers.Add("Cookie", $"{cookie.Name}={cookie.Value}");
            }

            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains("id=\"root\"", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
