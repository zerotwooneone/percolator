using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests.GroupLedger;

[TestFixture]
public class GroupConversationTests
{
    [Test]
    public void RenameGroup_WhenActorIsAdmin_UpdatesNameAndIncrementsEpoch()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var creatorId = new ParticipantId(Guid.NewGuid());
        var initialName = new GroupName("Initial Name");
        var avatarId = GroupAvatarId.FromBytesOwned(new byte[] { 1, 2, 3 });
        var conversation = GroupConversation.CreateNew(conversationId, initialName, creatorId, avatarId);
        var newName = new GroupName("New Name");

        // Act
        conversation.RenameGroup(creatorId, newName);

        // Assert
        Assert.That(conversation.Name, Is.EqualTo(newName));
        Assert.That(conversation.CurrentEpoch, Is.EqualTo(new GroupEpoch(2)));
    }

    [Test]
    public void RenameGroup_WhenActorIsNotAdmin_ThrowsUnauthorizedDomainException()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var creatorId = new ParticipantId(Guid.NewGuid());
        var memberId = new ParticipantId(Guid.NewGuid());
        var initialName = new GroupName("Initial Name");
        var avatarId = GroupAvatarId.FromBytesOwned(new byte[] { 1, 2, 3 });
        var conversation = GroupConversation.CreateNew(conversationId, initialName, creatorId, avatarId);
        conversation.AddMember(creatorId, memberId);
        var newName = new GroupName("New Name");

        // Act & Assert
        Assert.Throws<UnauthorizedDomainException>(() => conversation.RenameGroup(memberId, newName));
        Assert.That(conversation.Name, Is.EqualTo(initialName));
        Assert.That(conversation.CurrentEpoch, Is.EqualTo(new GroupEpoch(2))); // Epoch incremented by AddMember
    }

    [Test]
    public void LeaveGroup_WhenActorIsLastAdmin_ThrowsUnauthorizedDomainException()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var creatorId = new ParticipantId(Guid.NewGuid());
        var initialName = new GroupName("Initial Name");
        var avatarId = GroupAvatarId.FromBytesOwned(new byte[] { 1, 2, 3 });
        var conversation = GroupConversation.CreateNew(conversationId, initialName, creatorId, avatarId);

        // Act & Assert
        Assert.Throws<UnauthorizedDomainException>(() => conversation.LeaveGroup(creatorId));
        Assert.That(conversation.CurrentEpoch, Is.EqualTo(new GroupEpoch(1)));
    }

    [Test]
    public void AddMember_WhenMemberAlreadyExists_ThrowsUnauthorizedDomainException()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var creatorId = new ParticipantId(Guid.NewGuid());
        var memberId = new ParticipantId(Guid.NewGuid());
        var initialName = new GroupName("Initial Name");
        var avatarId = GroupAvatarId.FromBytesOwned(new byte[] { 1, 2, 3 });
        var conversation = GroupConversation.CreateNew(conversationId, initialName, creatorId, avatarId);
        conversation.AddMember(creatorId, memberId);

        // Act & Assert
        Assert.Throws<UnauthorizedDomainException>(() => conversation.AddMember(creatorId, memberId));
    }
}
