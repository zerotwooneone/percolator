using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;

namespace Percolator.Domain.Delivery.Events;

public sealed record EpochCommittedEvent(
    ChannelId ChannelId,
    EpochNumber NewEpoch,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
