using Percolator.Identity.Model;

namespace Percolator.Identity;

public interface ISelfIdentityRepository
{
    Task<SelfIdentity?> GetMostRecentAsync(CancellationToken ct = default);
    Task<SelfIdentity?> GetByIdAsync(SelfId id, CancellationToken ct = default);
    Task<IReadOnlyList<SelfIdentity>> ListAsync(CancellationToken ct = default);
    Task<SelfId> CreateAsync(SelfIdentity identity, CancellationToken ct = default);
    Task SaveAsync(SelfIdentity identity, CancellationToken ct = default);
}
