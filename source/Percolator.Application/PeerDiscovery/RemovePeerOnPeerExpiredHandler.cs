using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Sessions;

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
        var networkPeer = notification.Peer;
        _logger.LogInformation("- Peer expired: {IpAddress}:{Port}", networkPeer.IpAddress, networkPeer.GrpcEndpoint.Port);

        var identityPeer = await _peerRepository.GetByThumbprintAsync(networkPeer.Thumbprint);
        if (identityPeer is not null)
        {
            _connectionManager.RemovePeer(new PeerId(identityPeer.Id));
        }
        else
        {
            _logger.LogWarning("Could not find peer with thumbprint {Thumbprint} to remove from connection manager.", networkPeer.Thumbprint);
        }
    }
}
