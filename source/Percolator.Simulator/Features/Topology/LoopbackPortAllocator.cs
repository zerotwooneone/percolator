using System.Net;
using System.Net.Sockets;

namespace Percolator.Simulator.Features.Topology;

internal static class LoopbackPortAllocator
{
    public static int AllocateAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
