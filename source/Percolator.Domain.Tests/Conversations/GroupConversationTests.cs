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
