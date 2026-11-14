using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Delivery.Ports;

public interface IRelayRegistrationRepository
{
    Task<RelayRegistration?> GetByIdAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default);
    Task<IReadOnlyList<RelayRegistration>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RelayRegistration>> GetByOwnerIdentityAsync(PublicIdentityId ownerIdentityId, CancellationToken ct = default);
    Task SaveAsync(RelayRegistration registration, CancellationToken ct = default);
    Task DeleteAsync(PublicIdentityId relayIdentityId, CancellationToken ct = default);
}
