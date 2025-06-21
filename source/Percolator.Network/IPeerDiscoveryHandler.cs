using System.Threading.Tasks;

namespace Percolator.Network
{
    public interface IPeerDiscoveryHandler
    {
        Task HandlePeerDiscoveredAsync(Peer peer);
        Task HandlePeerExpiredAsync(Peer peer);
    }
}
