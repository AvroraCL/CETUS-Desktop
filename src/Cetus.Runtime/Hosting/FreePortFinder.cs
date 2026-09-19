using System.Net;
using System.Net.Sockets;

namespace Cetus.Hosting;

/// <summary>
/// Reserves a free loopback TCP port by asking the OS for an ephemeral one.
/// The port is released before returning; callers should start listening on
/// it immediately (same approach as the dev scripts).
/// </summary>
public static class FreePortFinder
{
    public static int Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
