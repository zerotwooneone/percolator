using Percolator.Network.ValueObjects;

namespace Percolator.Network;

public interface IPeerRoutingProfileRepository
{
    Task<PeerRoutingProfile?> GetByIdAsync(PeerId id, CancellationToken cancellationToken = default);
    Task UpsertAsync(PeerRoutingProfile aggregate, CancellationToken cancellationToken = default);
    Task<PeerRoutingProfile?> GetByPublicKeyAsync(IdentityPublicKey pk, CancellationToken cancellationToken = default);
    Task<IEnumerable<PeerRoutingProfile>> GetStaleAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default);
    Task<PeerRoutingProfile?> GetByPublicKeyHashAsync(PublicKeyHash publicKeyHash, CancellationToken cancellationToken = default);
}
