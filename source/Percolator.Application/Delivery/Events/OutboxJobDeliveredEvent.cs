using Percolator.Domain.Common;

namespace Percolator.Application.Delivery.Events;

public sealed record OutboxJobDeliveredEvent(
    Guid JobId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
