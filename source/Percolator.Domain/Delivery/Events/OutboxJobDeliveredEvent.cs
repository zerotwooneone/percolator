using Percolator.Domain.Common;

namespace Percolator.Domain.Delivery.Events;

public sealed record OutboxJobDeliveredEvent(
    Guid JobId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
