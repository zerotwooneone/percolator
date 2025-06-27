using Percolator.Contracts;
using Percolator.Sessions;

namespace Percolator.Application.PeerDiscovery;

public interface IPeerConnectionManager
{
    Task<TransportService.TransportServiceClient> GetTransportClient(PeerId peerId);
    void RemovePeer(PeerId peerId);
}
