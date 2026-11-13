namespace Percolator.Application.Ports;

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

public interface IPeerTransportServer : IAsyncDisposable
{
    bool IsRunning { get; }
    PortNumber? BoundPort { get; }
    Task StartAsync(
        IPAddress bindAddress,
        PortNumber? preferredPort = null,
        bool mapRelayEndpoints = false,
        CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
