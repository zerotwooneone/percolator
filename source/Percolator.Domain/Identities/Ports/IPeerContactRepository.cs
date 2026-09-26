using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Ports;

public interface IPeerContactRepository
{
    Task<PeerContact?> GetByPeerIdAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerContact>> GetAllForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default);
    Task SaveAsync(PeerContact contact, CancellationToken cancellationToken = default);
}
