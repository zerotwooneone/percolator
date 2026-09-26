using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Events;

public sealed record MemberRemovedEvent(
    ConversationId ConversationId,
    PublicIdentityId MemberId,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
