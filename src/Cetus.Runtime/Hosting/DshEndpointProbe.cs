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
    private readonly HttpClient _client;

    public DshEndpointProbe(Uri endpoint)
    {
        _endpoint = endpoint;
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
            using HttpResponseMessage response = await _client.GetAsync(_endpoint, cancellationToken);
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
