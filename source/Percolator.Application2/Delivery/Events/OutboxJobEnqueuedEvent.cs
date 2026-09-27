using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Delivery.Events;

public sealed record OutboxJobEnqueuedEvent(
    Guid JobId,
    PublicIdentityId OwnerIdentityId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
