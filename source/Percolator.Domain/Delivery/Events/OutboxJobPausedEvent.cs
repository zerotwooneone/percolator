using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Events;

public sealed record OutboxJobPausedEvent(
    Guid JobId,
    PublicIdentityId OwnerIdentityId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
