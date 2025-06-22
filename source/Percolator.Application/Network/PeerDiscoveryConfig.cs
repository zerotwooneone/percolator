using Percolator.Network;

namespace Percolator.Application.Network
{
    public class PeerDiscoveryConfig : IPeerDiscoveryConfig
    {
        public int BroadcastPort { get; set; }
        public int ListenPort { get; set; }
        public TimeSpan BroadcastInterval { get; set; }
        public TimeSpan PeerExpiration { get; set; }
    }
}
