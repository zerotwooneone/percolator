namespace Percolator.Application.Ports;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IPeerTransportServer : IAsyncDisposable
{
    bool IsRunning { get; }
    PortNumber? BoundPort { get; }
    Task StartAsync(PortNumber? preferredPort = null, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
