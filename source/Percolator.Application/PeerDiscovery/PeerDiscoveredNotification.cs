using MediatR;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery
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
