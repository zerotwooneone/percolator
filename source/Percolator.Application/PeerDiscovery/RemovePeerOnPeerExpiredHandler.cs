using MediatR;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.PeerDiscovery;

public class RemovePeerOnPeerExpiredHandler : INotificationHandler<PeerExpiredNotification>
{
    private readonly ILogger<RemovePeerOnPeerExpiredHandler> _logger;
    private readonly IPeerConnectionManager _connectionManager;

    public RemovePeerOnPeerExpiredHandler(ILogger<RemovePeerOnPeerExpiredHandler> logger, IPeerConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    public async Task Handle(PeerExpiredNotification notification, CancellationToken cancellationToken)
    {
        var networkPeer = notification.DiscoveredPeer;
        _logger.LogInformation("- Peer expired: discovery_key={DiscoveryKey}", networkPeer.DiscoveryKey.Value);
        // At discovery stage we don't have a bound PeerId. No removal from connection manager.
        await Task.CompletedTask;
    }
}
