using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery.Ports;

public interface IOutboxRepository
{
    Task<OutboxJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxJob>> GetDuePendingJobsAsync(DateTimeOffset asOfUtc, int limit = 50, CancellationToken ct = default);
    Task SaveAsync(OutboxJob job, CancellationToken ct = default);
}
