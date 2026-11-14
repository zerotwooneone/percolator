using Percolator.Domain.Common;
using Percolator.Domain.Identities;

namespace Percolator.Application.Delivery.Events;

public sealed record OutboxJobEnqueuedEvent(
    Guid JobId,
    PublicIdentityId OwnerIdentityId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
