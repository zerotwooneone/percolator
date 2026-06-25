using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.SeedWork;

namespace Percolator.Chat.Events;

/// <summary>
/// Domain event raised when a member is invited to a group.
/// </summary>
public sealed record MemberInvitedDomainEvent(
    Messaging.ValueObjects.ConversationId ConversationId,
    GroupParticipantId ParticipantId,
    ChatSenderKeyDistributionMessageBytes DistributionMessage,
    Pkh RelayPkh) : IDomainEvent;
