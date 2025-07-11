using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;

namespace Percolator.Application.PeerDiscovery;

public class RemovePeerOnPeerExpiredHandler : INotificationHandler<PeerExpiredNotification>
{
    private readonly ILogger<RemovePeerOnPeerExpiredHandler> _logger;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IPeerRepository _peerRepository;

    public RemovePeerOnPeerExpiredHandler(ILogger<RemovePeerOnPeerExpiredHandler> logger, IPeerConnectionManager connectionManager, IPeerRepository peerRepository)
    {
        _logger = logger;
        _connectionManager = connectionManager;
        _peerRepository = peerRepository;
    }

    public async Task Handle(PeerExpiredNotification notification, CancellationToken cancellationToken)
    {
        var networkPeer = notification.DiscoveredPeer;
        _logger.LogInformation("- Peer expired: {Endpoint}", networkPeer.GrpcEndpoint);

        var identityPeerId = new PeerId(networkPeer.Id.Value);
        await _peerRepository.RemoveAsync(identityPeerId);
        await _connectionManager.RemovePeer(identityPeerId);
    }
}
