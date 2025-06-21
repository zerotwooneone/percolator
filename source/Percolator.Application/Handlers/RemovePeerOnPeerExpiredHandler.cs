using MediatR;
using Percolator.Application.Notifications;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Handlers
{
    public class RemovePeerOnPeerExpiredHandler : INotificationHandler<PeerExpiredNotification>
    {
        private readonly PeerConnectionManager _connectionManager;

        public RemovePeerOnPeerExpiredHandler(PeerConnectionManager connectionManager)
        {
            _connectionManager = connectionManager;
        }

        public Task Handle(PeerExpiredNotification notification, CancellationToken cancellationToken)
        {
            var peer = notification.Peer;
            Console.WriteLine($"- Peer expired: {peer.IpAddress}:{peer.GrpcEndpoint.Port}");
            _connectionManager.RemovePeer(peer);
            return Task.CompletedTask;
        }
    }
}
