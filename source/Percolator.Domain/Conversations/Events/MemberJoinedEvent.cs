using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Events;

public sealed record MemberJoinedEvent(
    ConversationId ConversationId,
    PublicIdentityId MemberId,
    GroupRole Role,
    DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}
