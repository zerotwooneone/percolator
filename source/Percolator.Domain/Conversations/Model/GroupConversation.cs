using Percolator.Domain.Common;
using Percolator.Domain.Conversations.Events;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Model;

public sealed class GroupConversation : AggregateRoot<ConversationId>
{
    public override ConversationId Id { get; }
    public string Title { get; private set; }
    public EpochNumber CurrentEpoch { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }
    public MessageId? LastReadMessageId { get; private set; }

    private readonly List<GroupMember> _members = [];
    public IReadOnlyCollection<GroupMember> Members => _members.AsReadOnly();

    private readonly List<Message> _messages = [];
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    private GroupConversation(
        ConversationId id,
        string title,
        EpochNumber initialEpoch,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Title = title;
        CurrentEpoch = initialEpoch;
        CreatedAtUtc = createdAtUtc;
        LastActivityUtc = createdAtUtc;
    }

    public static DomainResult<GroupConversation> CreateGenesis(
        ConversationId id,
        PublicIdentityId creatorId,
        string title,
        IDateTimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return DomainResult<GroupConversation>.Failure(new DomainError("INVALID_TITLE", "Group title cannot be empty."));
        }

        var group = new GroupConversation(id, title, EpochNumber.Genesis, timeProvider.UtcNow);
        group._members.Add(new GroupMember(creatorId, GroupRole.Admin, timeProvider.UtcNow));

        return DomainResult<GroupConversation>.Success(group);
    }

    public DomainResult AddMember(
        PublicIdentityId actorId,
        PublicIdentityId newMemberId,
        GroupRole role,
        IDateTimeProvider timeProvider)
    {
        var actor = _members.FirstOrDefault(m => m.Id == actorId);
        if (actor == null || actor.Role != GroupRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only group admins can add new members."));
        }

        if (_members.Any(m => m.Id == newMemberId))
        {
            return DomainResult.Failure(new DomainError("MEMBER_ALREADY_EXISTS", "Member is already part of this group."));
        }

        var newMember = new GroupMember(newMemberId, role, timeProvider.UtcNow);
        _members.Add(newMember);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberJoinedEvent(Id, newMemberId, role, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult RemoveMember(
        PublicIdentityId actorId,
        PublicIdentityId targetMemberId,
        IDateTimeProvider timeProvider)
    {
        var actor = _members.FirstOrDefault(m => m.Id == actorId);
        if (actor == null || actor.Role != GroupRole.Admin)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only group admins can remove members."));
        }

        var memberToRemove = _members.FirstOrDefault(m => m.Id == targetMemberId);
        if (memberToRemove == null)
        {
            return DomainResult.Failure(new DomainError("MEMBER_NOT_FOUND", "Member does not exist in group."));
        }

        _members.Remove(memberToRemove);

        CurrentEpoch = CurrentEpoch.Next();
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MemberRemovedEvent(Id, targetMemberId, CurrentEpoch, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult AppendMessage(Message message, IDateTimeProvider timeProvider)
    {
        if (message.ConversationId != Id)
        {
            return DomainResult.Failure(new DomainError("CONVERSATION_MISMATCH", "Message does not belong to this group conversation."));
        }

        if (!_members.Any(m => m.Id == message.AuthorId))
        {
            return DomainResult.Failure(new DomainError("SENDER_NOT_MEMBER", "Message author is not an active member of this group."));
        }

        _messages.Add(message);
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new MessageAppendedEvent(Id, message.Id, LastActivityUtc));

        return DomainResult.Success();
    }

    public void Rebase(EpochNumber newEpoch, IEnumerable<GroupMember> currentMembers, IDateTimeProvider timeProvider)
    {
        CurrentEpoch = newEpoch;
        _members.Clear();
        _members.AddRange(currentMembers);
        LastActivityUtc = timeProvider.UtcNow;
    }

    public void MarkAsRead(MessageId messageId)
    {
        LastReadMessageId = messageId;
    }
}
