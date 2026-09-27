using Percolator.Domain.Common;
using Percolator.Domain.Conversations.Events;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Model;

public sealed class GroupConversation : AggregateRoot<ConversationId>
{
    public override ConversationId Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public string Title { get; private set; }
    public EpochNumber CurrentEpoch { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    private readonly List<GroupMember> _members = [];
    public IReadOnlyList<GroupMember> Members => _members.AsReadOnly();

    private readonly List<Message> _messages = [];
    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    private GroupConversation(
        ConversationId id,
        PublicIdentityId ownerIdentityId,
        string title,
        EpochNumber epoch,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OwnerIdentityId = ownerIdentityId;
        Title = title;
        CurrentEpoch = epoch;
        CreatedAtUtc = createdAtUtc;
        LastActivityUtc = createdAtUtc;
    }

    public static DomainResult<GroupConversation> CreateGenesis(
        ConversationId id,
        PublicIdentityId creatorId,
        string title,
        IDateTimeProvider timeProvider)
    {
        if (!id.IsValid)
        {
            return DomainResult<GroupConversation>.Failure(new DomainError("INVALID_CONVERSATION_ID", "ConversationId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return DomainResult<GroupConversation>.Failure(new DomainError("INVALID_TITLE", "Group title cannot be empty."));
        }

        var group = new GroupConversation(id, creatorId, title.Trim(), EpochNumber.Genesis, timeProvider.UtcNow);
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

        AddDomainEvent(new GroupEpochAdvancedEvent(Id, CurrentEpoch, LastActivityUtc));
        AddDomainEvent(new MemberJoinedEvent(Id, newMemberId, role, LastActivityUtc));

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

        AddDomainEvent(new GroupEpochAdvancedEvent(Id, CurrentEpoch, LastActivityUtc));
        AddDomainEvent(new MemberRemovedEvent(Id, targetMemberId, LastActivityUtc));

        return DomainResult.Success();
    }

    public DomainResult AppendMessage(Message message, IDateTimeProvider timeProvider)
    {
        if (message.ConversationId != Id)
        {
            return DomainResult.Failure(new DomainError("CONVERSATION_MISMATCH", "Message does not belong to this group conversation."));
        }

        _messages.Add(message);
        LastActivityUtc = timeProvider.UtcNow;

        // Note: In Signal Group V2, regular messages do NOT increment epoch
        AddDomainEvent(new MessageAppendedEvent(Id, message.Id, LastActivityUtc));

        return DomainResult.Success();
    }

    public void Rebase(EpochNumber latestEpoch, IEnumerable<GroupMember> latestMembers, IDateTimeProvider timeProvider)
    {
        CurrentEpoch = latestEpoch;
        _members.Clear();
        _members.AddRange(latestMembers);
        LastActivityUtc = timeProvider.UtcNow;
    }
}
