using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public interface IPeerRoutingProfileRepository
{
    Task<PeerRoutingProfile?> GetByIdAsync(NetworkPeerId id, CancellationToken cancellationToken = default);
    Task UpsertAsync(PeerRoutingProfile aggregate, CancellationToken cancellationToken = default);
}
