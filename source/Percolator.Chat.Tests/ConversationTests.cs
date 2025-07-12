using AutoFixture;
using FluentAssertions;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class ConversationTests
{
    private Fixture _fixture = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _fixture.Register<ParticipantId>(() => new ParticipantId(_fixture.Create<Guid>()));
        _fixture.Register<MessageId>(() => new MessageId(_fixture.Create<Guid>()));
        _fixture.Register<ConversationId>(() => new ConversationId(_fixture.Create<Guid>()));
    }

    [Test]
    public void Constructor_WithFewerThanTwoParticipants_ShouldThrowArgumentException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var channelId = _fixture.Create<ChannelId>();
        var participants = _fixture.CreateMany<ParticipantId>(1).ToList();

        // Act
        Action action = () => new Conversation(id, channelId, participants, _fixture.Create<IEnumerable<Message>>());

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_WithDuplicateParticipants_ShouldThrowArgumentException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participantId = _fixture.Create<ParticipantId>();
        var participants = new List<ParticipantId> { participantId, participantId };

        // Act
        Action action = () => new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_WithValidParticipants_ShouldCreateConversation()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();

        // Act
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());

        // Assert
        conversation.Should().NotBeNull();
        conversation.Id.Should().Be(id);
        conversation.Participants.Should().BeEquivalentTo(participants);
        conversation.Messages.Should().BeEmpty();
    }

    [Test]
    public void AddMessage_WhenSenderIsParticipant_ShouldAddMessageToConversation()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var content = _fixture.Create<string>();

        // Act
        conversation.AddMessage(senderId, content);

        // Assert
        conversation.Messages.Should().HaveCount(1);
        var message = conversation.Messages.First();
        message.SenderId.Should().Be(senderId);
        message.Content.Should().Be(content);
    }

    [Test]
    public void AddMessage_WhenSenderIsNotParticipant_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = _fixture.Create<ParticipantId>();
        var content = _fixture.Create<string>();

        // Act
        Action action = () => conversation.AddMessage(senderId, content);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddMessage_WithNullOrWhitespaceContent_ShouldThrowArgumentException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();

        // Act
        Action nullAction = () => conversation.AddMessage(senderId, null!);
        Action emptyAction = () => conversation.AddMessage(senderId, string.Empty);
        Action whitespaceAction = () => conversation.AddMessage(senderId, "   ");

        // Assert
        nullAction.Should().Throw<ArgumentException>();
        emptyAction.Should().Throw<ArgumentException>();
        whitespaceAction.Should().Throw<ArgumentException>();
    }

    [Test]
    public void AddParticipant_WhenNotAlreadyInConversation_ShouldAddParticipant()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var newParticipant = _fixture.Create<ParticipantId>();

        // Act
        conversation.AddParticipant(newParticipant);

        // Assert
        conversation.Participants.Should().HaveCount(3);
        conversation.Participants.Should().Contain(newParticipant);
    }

    [Test]
    public void AddParticipant_WhenAlreadyInConversation_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var existingParticipant = participants.First();

        // Act
        Action action = () => conversation.AddParticipant(existingParticipant);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RemoveParticipant_WhenParticipantExists_ShouldRemoveParticipant()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(3).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var participantToRemove = participants.First();

        // Act
        conversation.RemoveParticipant(participantToRemove);

        // Assert
        conversation.Participants.Should().HaveCount(2);
        conversation.Participants.Should().NotContain(participantToRemove);
    }

    [Test]
    public void RemoveParticipant_WhenParticipantDoesNotExist_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(3).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var nonExistentParticipant = _fixture.Create<ParticipantId>();

        // Act
        Action action = () => conversation.RemoveParticipant(nonExistentParticipant);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RemoveParticipant_WhenConversationHasOnlyTwoParticipants_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var participantToRemove = participants.First();

        // Act
        Action action = () => conversation.RemoveParticipant(participantToRemove);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddReaction_WhenMessageExistsAndUserIsParticipant_ShouldAddReactionToMessage()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var reactorId = participants.Last();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var message = conversation.Messages.First();

        // Act
        conversation.AddReaction(reactorId, message.Id, emoji);

        // Assert
        message.Reactions.Should().HaveCount(1);
        var reaction = message.Reactions.First();
        reaction.ReactorId.Should().Be(reactorId);
        reaction.Emoji.Should().Be(emoji);
    }

    [Test]
    public void AddReaction_WhenUserIsNotParticipant_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var nonParticipantId = _fixture.Create<ParticipantId>();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        Action action = () => conversation.AddReaction(nonParticipantId, messageId, emoji);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddReaction_WhenMessageDoesNotExist_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var reactorId = participants.First();
        var nonExistentMessageId = _fixture.Create<MessageId>();
        var emoji = "👍";

        // Act
        Action action = () => conversation.AddReaction(reactorId, nonExistentMessageId, emoji);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddReaction_WithNullOrWhitespaceEmoji_ShouldThrowArgumentException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var reactorId = participants.Last();
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        Action nullAction = () => conversation.AddReaction(reactorId, messageId, null!);
        Action emptyAction = () => conversation.AddReaction(reactorId, messageId, string.Empty);
        Action whitespaceAction = () => conversation.AddReaction(reactorId, messageId, "   ");

        // Assert
        nullAction.Should().Throw<ArgumentException>();
        emptyAction.Should().Throw<ArgumentException>();
        whitespaceAction.Should().Throw<ArgumentException>();
    }

    [Test]
    public void AddReaction_WhenReactionAlreadyExists_ShouldNotAddDuplicate()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var reactorId = participants.Last();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var message = conversation.Messages.First();
        conversation.AddReaction(reactorId, message.Id, emoji); // First reaction

        // Act
        conversation.AddReaction(reactorId, message.Id, emoji); // Duplicate reaction

        // Assert
        message.Reactions.Should().HaveCount(1);
    }

    [Test]
    public void RemoveReaction_WhenReactionExists_ShouldRemoveReactionFromMessage()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var reactorId = participants.Last();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var message = conversation.Messages.First();
        conversation.AddReaction(reactorId, message.Id, emoji);

        // Act
        conversation.RemoveReaction(reactorId, message.Id, emoji);

        // Assert
        message.Reactions.Should().BeEmpty();
    }

    [Test]
    public void RemoveReaction_WhenUserIsNotParticipant_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var nonParticipantId = _fixture.Create<ParticipantId>();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        Action action = () => conversation.RemoveReaction(nonParticipantId, messageId, emoji);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Constructor_WithName_ShouldCreateConversationWithName()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var name = _fixture.Create<string>();

        // Act
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>(), name);

        // Assert
        conversation.Name.Should().Be(name);
    }

    [Test]
    public void ChangeName_WhenCalled_ShouldUpdateConversationName()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var newName = _fixture.Create<string>();

        // Act
        conversation.ChangeName(newName);

        // Assert
        conversation.Name.Should().Be(newName);
    }

    [Test]
    public void MarkMessageAsRead_WhenMessageExistsAndUserIsParticipant_ShouldAddReadReceipt()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var readerId = participants.Last();
        conversation.AddMessage(senderId, "Hello");
        var message = conversation.Messages.First();

        // Act
        conversation.MarkMessageAsRead(readerId, message.Id);

        // Assert
        message.ReadReceipts.Should().HaveCount(1);
        var receipt = message.ReadReceipts.First();
        receipt.ReaderId.Should().Be(readerId);
        receipt.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Test]
    public void MarkMessageAsRead_WhenUserIsNotParticipant_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var nonParticipantId = _fixture.Create<ParticipantId>();
        conversation.AddMessage(participants.First(), "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        Action action = () => conversation.MarkMessageAsRead(nonParticipantId, messageId);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void MarkMessageAsRead_WhenMessageDoesNotExist_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var readerId = participants.First();
        var nonExistentMessageId = _fixture.Create<MessageId>();

        // Act
        Action action = () => conversation.MarkMessageAsRead(readerId, nonExistentMessageId);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void MarkMessageAsRead_WhenAlreadyRead_ShouldNotAddDuplicateReceipt()
    {
        // Arrange
        var id = _fixture.Create<ConversationId>();
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new Conversation(id,_fixture.Create<ChannelId>(), participants, _fixture.Create<IEnumerable<Message>>());
        var senderId = participants.First();
        var readerId = participants.Last();
        conversation.AddMessage(senderId, "Hello");
        var message = conversation.Messages.First();
        conversation.MarkMessageAsRead(readerId, message.Id); // First read

        // Act
        conversation.MarkMessageAsRead(readerId, message.Id); // Second read

        // Assert
        message.ReadReceipts.Should().HaveCount(1);
    }
}
