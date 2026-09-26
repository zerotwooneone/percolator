using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;

namespace Percolator.Domain.Conversations.Events;

public sealed record MessageAppendedEvent(
    ConversationId ConversationId,
    MessageId MessageId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
