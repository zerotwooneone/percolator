using Percolator.Application.Ports;

namespace Percolator.Application.Delivery.Events;

public sealed record RelayProfileSlotsPrunedEvent(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    int PurgedCount) : IApplicationEvent;
