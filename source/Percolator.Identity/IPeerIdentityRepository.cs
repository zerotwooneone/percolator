using Percolator.Identity.Model;

namespace Percolator.Identity;

public interface IPeerIdentityRepository
{
    Task<PeerIdentity?> GetByIdAsync(PeerId id, CancellationToken ct = default);
    Task<PeerIdentity?> GetByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct = default);
    Task<PeerIdentity?> FindByPublicKeyHashAsync(IdentityPublicKeyHash fingerprint, CancellationToken ct = default);
    Task<PeerIdentity> GetOrCreateAsync(PublicIdentityId publicIdentityId, CancellationToken ct = default);
    Task SaveAsync(PeerIdentity peer, CancellationToken ct = default);
}
