using MediatR;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.PeerDiscovery
{
    public class RemovePeerOnPeerExpiredHandler : INotificationHandler<PeerExpiredNotification>
    {
        private readonly ILogger<RemovePeerOnPeerExpiredHandler> _logger;
        private readonly PeerConnectionManager _connectionManager;

        public RemovePeerOnPeerExpiredHandler(ILogger<RemovePeerOnPeerExpiredHandler> logger, PeerConnectionManager connectionManager)
        {
            _logger = logger;
            _connectionManager = connectionManager;
        }

        public Task Handle(PeerExpiredNotification notification, CancellationToken cancellationToken)
        {
            var peer = notification.Peer;
            _logger.LogInformation("- Peer expired: {IpAddress}:{Port}", peer.IpAddress, peer.GrpcEndpoint.Port);
            _connectionManager.RemovePeer(peer);
            return Task.CompletedTask;
        }
    }
}
