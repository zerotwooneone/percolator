using Percolator.PluginSdk.Events;

namespace Percolator.Application.Delivery.Events;

public sealed record RelayPreKeysPrunedEvent(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    int PurgedCount) : IApplicationEvent;
