using Percolator.Application.Ports;

namespace Percolator.Application.Delivery.Events;

public sealed record OutboxJobsPrunedEvent(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    int PurgedCount) : IApplicationEvent;
