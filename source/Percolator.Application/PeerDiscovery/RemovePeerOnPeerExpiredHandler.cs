using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;

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
        _logger.LogInformation("- Peer expired: {Endpoint}", networkPeer.GrpcEndpoint);

        var identityPeerId = new PeerId(networkPeer.Id.Value);
        await _connectionManager.RemovePeer(identityPeerId).ConfigureAwait(false);
    }
}
