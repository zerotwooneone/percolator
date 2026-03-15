using Percolator.Identity.Model;

namespace Percolator.Identity;

public interface IPeerIdentityRepository
{
    Task<PeerIdentity?> GetByIdAsync(PeerId id, CancellationToken ct = default);
    Task<PeerIdentity?> GetByNameAsync(DisplayName name, CancellationToken ct = default);
    Task<PeerIdentity?> FindByPublicKeyHashAsync(byte[] fingerprint, CancellationToken ct = default);
    Task SaveAsync(PeerIdentity peer, CancellationToken ct = default);
}
