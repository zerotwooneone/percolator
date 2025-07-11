using MediatR;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery
{
    public class PeerDiscoveredNotification : INotification
    {
        public DiscoveredPeer DiscoveredPeer { get; }

        public PeerDiscoveredNotification(DiscoveredPeer discoveredPeer)
        {
            DiscoveredPeer = discoveredPeer;
        }
    }
}
