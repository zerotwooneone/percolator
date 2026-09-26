using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;

namespace Percolator.Domain.Delivery.Events;

public sealed record EpochCommittedEvent(
    ConversationId ConversationId,
    EpochNumber NewEpoch,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
