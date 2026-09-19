using System.Net;
using System.Net.Sockets;
using Cetus.Hosting;
using Xunit;

namespace Cetus.Desktop.Tests;

/// <summary>
/// Fast (process-free) port self-heal coverage: the occupied-port exception
/// surface and the free-port resolver. Full host lifecycle runs in the
/// Integration suite.
/// </summary>
public sealed class DshPortSelfHealTests
{
    [Fact]
    public async Task StartAsync_ForeignListenerOnPort_ThrowsPortOccupied()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var host = new DshHost(
            new DshCommand(null, null, UseShim: false),
            $"http://127.0.0.1:{port}/",
            dshHomeOverride: null,
            portOccupiedGraceSeconds: 0);

        DshPortOccupiedException error = await Assert.ThrowsAsync<DshPortOccupiedException>(
            () => host.StartAsync());

        Assert.Equal(port, error.Port);
    }

    [Fact]
    public void Reserve_ReturnsDistinctUsablePorts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int held = ((IPEndPoint)listener.LocalEndpoint).Port;

        int first = FreePortFinder.Reserve();
        int second = FreePortFinder.Reserve();

        Assert.InRange(first, 1, 65535);
        Assert.InRange(second, 1, 65535);
        Assert.NotEqual(held, first);
        Assert.NotEqual(first, second);
    }
}
