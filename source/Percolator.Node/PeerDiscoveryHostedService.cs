using Microsoft.Extensions.Hosting;
using Percolator.Network;

namespace Percolator.Node;

/// <summary>
/// An adapter that allows the domain's PeerDiscoveryService to be run as a background
/// IHostedService within the .NET generic host, separating domain logic from hosting concerns.
/// </summary>
public class PeerDiscoveryHostedService : IHostedService
{
    private readonly IPeerDiscoveryService _peerDiscoveryService;

    public PeerDiscoveryHostedService(IPeerDiscoveryService peerDiscoveryService)
    {
        _peerDiscoveryService = peerDiscoveryService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _peerDiscoveryService.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _peerDiscoveryService.Stop();
        return Task.CompletedTask;
    }
}
