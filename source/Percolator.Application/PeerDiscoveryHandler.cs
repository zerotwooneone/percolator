using MediatR;
using Percolator.Application.Notifications;
using Percolator.Network;

namespace Percolator.Application
{
    public class PeerDiscoveryHandler : IPeerDiscoveryHandler
    {
        private readonly IMediator _mediator;

        public PeerDiscoveryHandler(IMediator mediator)
        {
            _mediator = mediator;
        }

        public Task HandlePeerDiscoveredAsync(Peer peer)
        {
            return _mediator.Publish(new PeerDiscoveredNotification(peer));
        }

        public Task HandlePeerExpiredAsync(Peer peer)
        {
            return _mediator.Publish(new PeerExpiredNotification(peer));
        }
    }
}
