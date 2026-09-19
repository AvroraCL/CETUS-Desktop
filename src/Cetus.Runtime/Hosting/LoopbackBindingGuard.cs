using System.Net;
using System.Net.NetworkInformation;

namespace Cetus.Hosting;

/// <summary>
/// Verifies that a port is bound to loopback only. Defense in depth on top
/// of the explicit --host 127.0.0.1 sidecar argument: if a future DSH
/// version starts listening on all interfaces, startup fails loudly instead
/// of silently exposing the Harness to the network.
/// </summary>
public static class LoopbackBindingGuard
{
    /// <summary>
    /// True when the port is not listening at all or listens on loopback
    /// only; false when any wildcard (0.0.0.0 / ::) binding is present.
    /// </summary>
    public static bool IsLoopbackOnly(int port)
    {
        foreach (IPEndPoint endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
        {
            if (endpoint.Port != port)
            {
                continue;
            }

            if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                return false;
            }
        }

        return true;
    }
}
