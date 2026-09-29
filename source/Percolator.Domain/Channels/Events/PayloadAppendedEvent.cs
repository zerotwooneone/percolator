using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;

namespace Percolator.Domain.Channels.Events;

public sealed record PayloadAppendedEvent(
    ChannelId ChannelId,
    PayloadId PayloadId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
