using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Channels;

[TestFixture]
public class GroupChannelTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private PublicIdentityId _ownerId;
    private PublicIdentityId _adminId;
    private PublicIdentityId _regularMemberId;
    private ChannelId _channelId;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _ownerId = PublicIdentityId.New();
        _adminId = _ownerId;
        _regularMemberId = PublicIdentityId.New();
        _channelId = ChannelId.New();
    }

    [Test]
    public void AddMember_ByAdmin_AddsMember_IncrementsEpoch_AndEmitsMemberJoinedEvent()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        group.ClearDomainEvents();

        var newMemberId = PublicIdentityId.New();
        var result = group.AddMember(
            actorId: _adminId,
            newMemberId: newMemberId,
            role: ChannelRole.Member,
            _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.CurrentEpoch.Value.Should().Be(1);
        group.Members.Should().Contain(m => m.Id == newMemberId && m.Role == ChannelRole.Member);

        group.DomainEvents.Should().ContainSingle(e => e is MemberJoinedEvent);
        var joinedEvent = (MemberJoinedEvent)group.DomainEvents.Single();
        joinedEvent.ChannelId.Should().Be(_channelId);
        joinedEvent.MemberId.Should().Be(newMemberId);
        joinedEvent.Role.Should().Be(ChannelRole.Member);
        joinedEvent.NewEpoch.Should().Be(group.CurrentEpoch);
    }

    [Test]
    public void AddMember_WhenMemberAlreadyExists_ReturnsMemberAlreadyExistsError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var result = group.AddMember(
            actorId: _adminId,
            newMemberId: _ownerId,
            role: ChannelRole.Member,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MEMBER_ALREADY_EXISTS");
        group.CurrentEpoch.Value.Should().Be(0);
    }

    [Test]
    public void AddMember_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        // Add regular member first
        var nonAdmin = PublicIdentityId.New();
        group.AddMember(_adminId, nonAdmin, ChannelRole.Member, _timeProvider);
        group.ClearDomainEvents();

        // Non-admin attempts to invite someone
        var stranger = PublicIdentityId.New();
        var result = group.AddMember(
            actorId: nonAdmin,
            newMemberId: stranger,
            role: ChannelRole.Member,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
        group.CurrentEpoch.Value.Should().Be(1); // unchanged
    }

    [Test]
    public void RemoveMember_ByAdmin_RemovesMember_AdvancesEpoch_AndEmitsMemberRemovedEvent()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, ChannelRole.Member, _timeProvider);
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
        var group = GroupChannel.CreateGenesis(
            _channelId,
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
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var member1 = PublicIdentityId.New();
        var member2 = PublicIdentityId.New();
        group.AddMember(_adminId, member1, ChannelRole.Member, _timeProvider);
        group.AddMember(_adminId, member2, ChannelRole.Member, _timeProvider);

        var result = group.RemoveMember(member1, member2, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
    }

    [Test]
    public void RemoveMember_WhenMemberNotFound_ReturnsMemberNotFoundError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var ghostId = PublicIdentityId.New();
        var result = group.RemoveMember(_adminId, ghostId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MEMBER_NOT_FOUND");
    }

    [Test]
    public void RenameChannel_ByAdmin_UpdatesName_AdvancesEpoch_AndEmitsChannelRenamedEvent()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Initial Title",
            _timeProvider).Value;

        group.ClearDomainEvents();

        var result = group.RenameChannel(_adminId, "Updated Title", _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.Name.Should().Be("Updated Title");
        group.CurrentEpoch.Value.Should().Be(1);

        group.DomainEvents.Should().ContainSingle(e => e is ChannelRenamedEvent);
        var renamedEvent = (ChannelRenamedEvent)group.DomainEvents.Single();
        renamedEvent.NewName.Should().Be("Updated Title");
        renamedEvent.ActorId.Should().Be(_adminId);
    }

    [Test]
    public void RenameChannel_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Initial Title",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, ChannelRole.Member, _timeProvider);

        var result = group.RenameChannel(memberId, "Malicious Rename", _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
        group.Name.Should().Be("Initial Title");
    }

    [Test]
    public void RenameChannel_WithEmptyName_ReturnsInvalidNameError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Initial Title",
            _timeProvider).Value;

        var result = group.RenameChannel(_adminId, "   ", _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_NAME");
    }

    [Test]
    public void ChangeMemberRole_ByAdmin_PromotesMember_AdvancesEpoch_AndEmitsMemberRoleChangedEvent()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, ChannelRole.Member, _timeProvider);
        group.ClearDomainEvents();

        var result = group.ChangeMemberRole(_adminId, memberId, ChannelRole.Admin, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.Members.First(m => m.Id == memberId).Role.Should().Be(ChannelRole.Admin);
        group.CurrentEpoch.Value.Should().Be(2);

        group.DomainEvents.Should().ContainSingle(e => e is ChannelMemberRoleChangedEvent);
        var changedEvent = (ChannelMemberRoleChangedEvent)group.DomainEvents.Single();
        changedEvent.TargetMemberId.Should().Be(memberId);
        changedEvent.NewRole.Should().Be(ChannelRole.Admin);
    }

    [Test]
    public void ChangeMemberRole_DemotingLastAdmin_ReturnsLastAdminCannotBeDemotedError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var result = group.ChangeMemberRole(_adminId, _adminId, ChannelRole.Member, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LAST_ADMIN_CANNOT_BE_DEMOTED");
        group.Members.First(m => m.Id == _adminId).Role.Should().Be(ChannelRole.Admin);
    }

    [Test]
    public void ChangeMemberRole_ByNonAdmin_ReturnsUnauthorizedRoleError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, ChannelRole.Member, _timeProvider);

        var result = group.ChangeMemberRole(memberId, _adminId, ChannelRole.Member, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
    }

    [Test]
    public void LeaveChannel_WhenRegularMemberLeaves_RemovesMember_AndAdvancesEpoch()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, ChannelRole.Member, _timeProvider);
        group.ClearDomainEvents();

        var result = group.LeaveChannel(memberId, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.Members.Should().NotContain(m => m.Id == memberId);
        group.CurrentEpoch.Value.Should().Be(2);

        group.DomainEvents.Should().ContainSingle(e => e is MemberRemovedEvent);
    }

    [Test]
    public void LeaveChannel_WhenLastAdminLeavesAndOtherMembersExist_ReturnsLastAdminCannotLeaveError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var memberId = PublicIdentityId.New();
        group.AddMember(_adminId, memberId, ChannelRole.Member, _timeProvider);

        var result = group.LeaveChannel(_adminId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LAST_ADMIN_CANNOT_LEAVE");
    }

    [Test]
    public void AppendPayload_AppendsPayload_WithoutChangingEpoch()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var payload = new ChannelPayload(
            PayloadId.New(),
            group.Id,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow);

        var result = group.AppendPayload(payload, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        group.CurrentEpoch.Value.Should().Be(0); // Epoch must NOT change on payload
        group.DomainEvents.Should().ContainSingle(e => e is PayloadAppendedEvent);
    }

    [Test]
    public void AppendPayload_WithMismatchedChannelId_ReturnsError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var wrongChannelId = ChannelId.New();
        var payload = new ChannelPayload(
            PayloadId.New(),
            wrongChannelId,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow);

        var result = group.AppendPayload(payload, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CHANNEL_MISMATCH");
    }

    [Test]
    public void AppendPayload_WhenSenderNotMember_ReturnsSenderNotMemberError()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var strangerId = PublicIdentityId.New();
        var payload = new ChannelPayload(
            PayloadId.New(),
            group.Id,
            strangerId,
            DeviceId.Primary,
            new byte[] { 1, 2, 3 },
            _timeProvider.UtcNow);

        var result = group.AppendPayload(payload, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SENDER_NOT_MEMBER");
    }

    [Test]
    public void Rebase_WhenConflictOccurs_ReplacesLocalEpochAndRoster()
    {
        var group = GroupChannel.CreateGenesis(
            _channelId,
            _ownerId,
            "Security Team",
            _timeProvider).Value;

        var remoteMember = new ChannelMember(PublicIdentityId.New(), ChannelRole.Member, _timeProvider.UtcNow);
        var latestEpoch = new EpochNumber(5);

        group.Rebase(latestEpoch, [new ChannelMember(_ownerId, ChannelRole.Admin, _timeProvider.UtcNow), remoteMember], _timeProvider);

        group.CurrentEpoch.Should().Be(latestEpoch);
        group.Members.Should().HaveCount(2);
    }
}
