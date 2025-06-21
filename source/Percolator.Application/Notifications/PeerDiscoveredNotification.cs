using MediatR;
using Percolator.Network;

namespace Percolator.Application.Notifications
{
    public class PeerDiscoveredNotification : INotification
    {
        public Peer Peer { get; }

        public PeerDiscoveredNotification(Peer peer)
        {
            Peer = peer;
        }
    }
}
