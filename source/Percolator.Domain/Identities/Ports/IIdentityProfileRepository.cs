using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.Ports;

public interface IIdentityProfileRepository
{
    Task<IdentityProfile?> GetByIdAsync(PublicIdentityId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IdentityProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IdentityProfile profile, CancellationToken cancellationToken = default);
}
