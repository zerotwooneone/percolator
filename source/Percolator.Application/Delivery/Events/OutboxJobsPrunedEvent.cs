namespace Percolator.Application2.Delivery.Events;

using Percolator.Application2.Ports;

public sealed record OutboxJobsPrunedEvent(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    int PurgedCount) : IApplicationEvent;
