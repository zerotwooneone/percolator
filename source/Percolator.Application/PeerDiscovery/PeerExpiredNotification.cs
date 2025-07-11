using MediatR;
using Percolator.Network;

namespace Percolator.Application.PeerDiscovery
{
    public class PeerExpiredNotification : INotification
    {
        public DiscoveredPeer DiscoveredPeer { get; }

        public PeerExpiredNotification(DiscoveredPeer discoveredPeer)
        {
            DiscoveredPeer = discoveredPeer;
        }
    }
}
