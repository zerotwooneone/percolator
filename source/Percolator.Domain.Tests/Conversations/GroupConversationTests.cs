using Percolator.Domain.Conversations.Events;
using Percolator.Domain.Conversations.Model;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Conversations;

[TestFixture]
public class GroupConversationTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private PublicIdentityId _ownerId;
    private PublicIdentityId _adminId;
    private PublicIdentityId _regularMemberId;
    private ConversationId _conversationId;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _ownerId = PublicIdentityId.New();
        _adminId = _ownerId;
        _regularMemberId = PublicIdentityId.New();
        _conversationId = ConversationId.New();
    }

    [Test]
    public void AddMember_ByAdmin_AddsMember_IncrementsEpoch_AndEmitsMemberJoinedEvent()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        group.ClearDomainEvents();

        var newMemberId = PublicIdentityId.New();
        var result = group.AddMember(
            actorId: _adminId,
            newMemberId: newMemberId,
            role: GroupRole.Member,
            _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.CurrentEpoch.Value.Should().Be(1);
        group.Members.Should().Contain(m => m.Id == newMemberId && m.Role == GroupRole.Member);

        group.DomainEvents.Should().ContainSingle(e => e is MemberJoinedEvent);
        var joinedEvent = (MemberJoinedEvent)group.DomainEvents.Single();
        joinedEvent.ConversationId.Should().Be(_conversationId);
        joinedEvent.MemberId.Should().Be(newMemberId);
        joinedEvent.Role.Should().Be(GroupRole.Member);
        joinedEvent.NewEpoch.Should().Be(group.CurrentEpoch);
    }

    [Test]
    public void AddMember_WhenMemberAlreadyExists_ReturnsMemberAlreadyExistsError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var result = group.AddMember(
            actorId: _adminId,
            newMemberId: _ownerId,
            role: GroupRole.Member,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MEMBER_ALREADY_EXISTS");
        group.CurrentEpoch.Value.Should().Be(0);
    }

    [Test]
    public void AddMember_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        // Add regular member first
        var nonAdmin = PublicIdentityId.New();
        group.AddMember(_adminId, nonAdmin, GroupRole.Member, _timeProvider);
        group.ClearDomainEvents();

        // Non-admin attempts to invite someone
        var stranger = PublicIdentityId.New();
        var result = group.AddMember(
            actorId: nonAdmin,
            newMemberId: stranger,
            role: GroupRole.Member,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
        group.CurrentEpoch.Value.Should().Be(1); // unchanged
    }

    [Test]
    public void RemoveMember_ByAdmin_RemovesMember_AdvancesEpoch_AndEmitsMemberRemovedEvent()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, GroupRole.Member, _timeProvider);
        group.ClearDomainEvents();

        var result = group.RemoveMember(_adminId, memberId, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.CurrentEpoch.Value.Should().Be(2);
        group.Members.Should().NotContain(m => m.Id == memberId);

        group.DomainEvents.Should().ContainSingle(e => e is MemberRemovedEvent);
        var removedEvent = (MemberRemovedEvent)group.DomainEvents.Single();
        removedEvent.MemberId.Should().Be(memberId);
        removedEvent.NewEpoch.Should().Be(group.CurrentEpoch);
    }

    [Test]
    public void RemoveMember_WhenTargetIsLastAdmin_ReturnsCannotRemoveLastAdminError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var result = group.RemoveMember(_adminId, _adminId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CANNOT_REMOVE_LAST_ADMIN");
    }

    [Test]
    public void RemoveMember_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var member1 = PublicIdentityId.New();
        var member2 = PublicIdentityId.New();
        group.AddMember(_adminId, member1, GroupRole.Member, _timeProvider);
        group.AddMember(_adminId, member2, GroupRole.Member, _timeProvider);

        var result = group.RemoveMember(member1, member2, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
    }

    [Test]
    public void RemoveMember_WhenMemberNotFound_ReturnsMemberNotFoundError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var ghostId = PublicIdentityId.New();
        var result = group.RemoveMember(_adminId, ghostId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MEMBER_NOT_FOUND");
    }

    [Test]
    public void RenameGroup_ByAdmin_UpdatesTitle_AdvancesEpoch_AndEmitsGroupRenamedEvent()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Initial Title",
            _timeProvider).Value;

        group.ClearDomainEvents();

        var result = group.RenameGroup(_adminId, "Updated Title", _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.Title.Should().Be("Updated Title");
        group.CurrentEpoch.Value.Should().Be(1);

        group.DomainEvents.Should().ContainSingle(e => e is GroupRenamedEvent);
        var renamedEvent = (GroupRenamedEvent)group.DomainEvents.Single();
        renamedEvent.NewTitle.Should().Be("Updated Title");
        renamedEvent.ActorId.Should().Be(_adminId);
    }

    [Test]
    public void RenameGroup_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Initial Title",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, GroupRole.Member, _timeProvider);

        var result = group.RenameGroup(memberId, "Malicious Rename", _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
        group.Title.Should().Be("Initial Title");
    }

    [Test]
    public void RenameGroup_WithEmptyTitle_ReturnsInvalidTitleError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Initial Title",
            _timeProvider).Value;

        var result = group.RenameGroup(_adminId, "   ", _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_TITLE");
    }

    [Test]
    public void ChangeMemberRole_ByAdmin_PromotesMember_AdvancesEpoch_AndEmitsMemberRoleChangedEvent()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, GroupRole.Member, _timeProvider);
        group.ClearDomainEvents();

        var result = group.ChangeMemberRole(_adminId, memberId, GroupRole.Admin, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.Members.First(m => m.Id == memberId).Role.Should().Be(GroupRole.Admin);
        group.CurrentEpoch.Value.Should().Be(2);

        group.DomainEvents.Should().ContainSingle(e => e is MemberRoleChangedEvent);
        var changedEvent = (MemberRoleChangedEvent)group.DomainEvents.Single();
        changedEvent.TargetMemberId.Should().Be(memberId);
        changedEvent.NewRole.Should().Be(GroupRole.Admin);
    }

    [Test]
    public void ChangeMemberRole_DemotingLastAdmin_ReturnsLastAdminCannotBeDemotedError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var result = group.ChangeMemberRole(_adminId, _adminId, GroupRole.Member, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LAST_ADMIN_CANNOT_BE_DEMOTED");
        group.Members.First(m => m.Id == _adminId).Role.Should().Be(GroupRole.Admin);
    }

    [Test]
    public void ChangeMemberRole_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, GroupRole.Member, _timeProvider);

        var result = group.ChangeMemberRole(memberId, _adminId, GroupRole.Member, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
    }

    [Test]
    public void LeaveGroup_WhenRegularMemberLeaves_RemovesMember_AndAdvancesEpoch()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, GroupRole.Member, _timeProvider);
        group.ClearDomainEvents();

        var result = group.LeaveGroup(memberId, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.Members.Should().NotContain(m => m.Id == memberId);
        group.CurrentEpoch.Value.Should().Be(2);

        group.DomainEvents.Should().ContainSingle(e => e is MemberRemovedEvent);
    }

    [Test]
    public void LeaveGroup_WhenLastAdminLeavesAndOtherMembersExist_ReturnsLastAdminCannotLeaveError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, GroupRole.Member, _timeProvider);

        var result = group.LeaveGroup(_adminId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LAST_ADMIN_CANNOT_LEAVE");
    }

    [Test]
    public void AppendMessage_AppendsMessage_WithoutChangingEpoch()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var message = new Message(
            MessageId.New(),
            group.Id,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow);

        var result = group.AppendMessage(message, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.CurrentEpoch.Value.Should().Be(0); // Epoch must NOT change on message
        group.DomainEvents.Should().ContainSingle(e => e is MessageAppendedEvent);
    }

    [Test]
    public void AppendMessage_WithMismatchedConversationId_ReturnsError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var wrongConvId = ConversationId.New();
        var message = new Message(
            MessageId.New(),
            wrongConvId,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow);

        var result = group.AppendMessage(message, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONVERSATION_MISMATCH");
    }

    [Test]
    public void AppendMessage_WhenSenderNotMember_ReturnsSenderNotMemberError()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var strangerId = PublicIdentityId.New();
        var message = new Message(
            MessageId.New(),
            group.Id,
            strangerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow);

        var result = group.AppendMessage(message, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SENDER_NOT_MEMBER");
    }

    [Test]
    public void Rebase_WhenConflictOccurs_ReplacesLocalEpochAndRoster()
    {
        var group = GroupConversation.CreateGenesis(
            _conversationId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var remoteMember = new GroupMember(PublicIdentityId.New(), GroupRole.Member, _timeProvider.UtcNow);
        var latestEpoch = new EpochNumber(5);

        group.Rebase(latestEpoch, [new GroupMember(_ownerId, GroupRole.Admin, _timeProvider.UtcNow), remoteMember], _timeProvider);

        group.CurrentEpoch.Should().Be(latestEpoch);
        group.Members.Should().HaveCount(2);
    }
}
