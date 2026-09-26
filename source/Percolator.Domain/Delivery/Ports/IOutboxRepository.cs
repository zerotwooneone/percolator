using Percolator.Domain.Delivery.Client;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Ports;

public interface IOutboxRepository
{
    Task<OutboxJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxJob>> GetPendingJobsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default);
    Task SaveAsync(OutboxJob job, CancellationToken cancellationToken = default);
}
