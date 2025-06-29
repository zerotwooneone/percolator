using Percolator.Contracts;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.PeerDiscovery;

public interface IPeerConnectionManager
{
    Task<TransportService.TransportServiceClient> GetTransportClient(IdentityPeerId peerId);
    Task RemovePeer(IdentityPeerId peerId);
}
