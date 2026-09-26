using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;

namespace Percolator.Domain.Conversations.Events;

public sealed record GroupEpochAdvancedEvent(
    ConversationId ConversationId,
    EpochNumber NewEpoch,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
