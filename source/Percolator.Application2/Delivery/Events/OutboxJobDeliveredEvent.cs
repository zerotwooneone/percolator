using Percolator.Domain.Common;

namespace Percolator.Application2.Delivery.Events;

public sealed record OutboxJobDeliveredEvent(
    Guid JobId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
