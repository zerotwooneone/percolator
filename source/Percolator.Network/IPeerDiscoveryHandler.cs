namespace Percolator.Network
{
    public interface IPeerDiscoveryHandler
    {
        Task HandlePeerDiscoveredAsync(DiscoveredPeer discoveredPeer);
        Task HandlePeerExpiredAsync(DiscoveredPeer discoveredPeer);
    }
}
