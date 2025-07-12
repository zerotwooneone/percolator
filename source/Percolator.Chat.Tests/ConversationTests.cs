using AutoFixture;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

[TestFixture]
public class ConversationTests
{
    private IFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
    }

    [Test]
    public void Constructor_WhenLessThanTwoParticipants_ThrowsArgumentException()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(1).ToList();

        // Act
        Action action = () => new ConversationBuilder().WithParticipants(participants).Build();

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_WhenDuplicateParticipants_ThrowsArgumentException()
    {
        // Arrange
        var participantId = _fixture.Create<ParticipantId>();
        var participants = new List<ParticipantId> { participantId, participantId };

        // Act
        Action action = () => new ConversationBuilder().WithParticipants(participants).Build();

        // Assert
        action.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_WithValidArguments_ShouldCreateInstance()
    {
        // Arrange & Act
        var conversation = new ConversationBuilder().Build();

        // Assert
        conversation.Should().NotBeNull();
        conversation.Participants.Should().HaveCount(2);
    }

    [Test]
    public void AddMessage_WithValidSenderAndContent_ShouldAddMessageToList()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var senderId = participants.First();
        var content = _fixture.Create<string>();

        // Act
        conversation.AddMessage(senderId, content);

        // Assert
        conversation.Messages.Should().HaveCount(1);
        conversation.Messages.First().Content.Should().Be(content);
    }

    [Test]
    public void AddMessage_WhenSenderIsNotParticipant_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var nonParticipantId = _fixture.Create<ParticipantId>();
        var content = _fixture.Create<string>();

        // Act
        Action action = () => conversation.AddMessage(nonParticipantId, content);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddParticipant_WithNewParticipant_ShouldSucceed()
    {
        // Arrange
        var conversation = new ConversationBuilder().WithParticipants(_fixture.CreateMany<ParticipantId>(2).ToArray()).Build();
        var newParticipant = _fixture.Create<ParticipantId>();

        // Act
        conversation.AddParticipant(newParticipant);

        // Assert
        conversation.Participants.Should().HaveCount(3);
        conversation.Participants.Should().Contain(newParticipant);
    }

    [Test]
    public void AddParticipant_WhenParticipantExists_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var existingParticipant = conversation.Participants.First();

        // Act
        Action action = () => conversation.AddParticipant(existingParticipant);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RemoveParticipant_WhenParticipantExists_ShouldSucceed()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(3).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var participantToRemove = participants.First();

        // Act
        conversation.RemoveParticipant(participantToRemove);

        // Assert
        conversation.Participants.Should().HaveCount(2);
        conversation.Participants.Should().NotContain(participantToRemove);
    }

    [Test]
    public void RemoveParticipant_WhenParticipantIsTheSenderOfAMessage_ShouldSucceed()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(3).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var senderId = participants.First();
        conversation.AddMessage(senderId, "Hello");

        // Act
        conversation.RemoveParticipant(senderId);

        // Assert
        conversation.Participants.Should().HaveCount(2);
        conversation.Participants.Should().NotContain(senderId);
    }

    [Test]
    public void RemoveParticipant_WhenParticipantDoesNotExist_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var nonParticipantId = _fixture.Create<ParticipantId>();

        // Act
        Action action = () => conversation.RemoveParticipant(nonParticipantId);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RemoveParticipant_WhenRemovingLastParticipant_ThrowsInvalidOperationException()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();

        // Act
        Action action = () =>
        {
            conversation.RemoveParticipant(participants[0]);
            conversation.RemoveParticipant(participants[1]);
        };

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddReaction_WithValidParticipantAndMessage_ShouldAddReaction()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var senderId = participants.First();
        var reactorId = participants.Last();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        conversation.AddReaction(reactorId, messageId, emoji);

        // Assert
        var reaction = conversation.Messages.First().Reactions.First();
        reaction.Emoji.Should().Be(emoji);
        reaction.ReactorId.Should().Be(reactorId);
    }

    [Test]
    public void AddReaction_WhenParticipantIsNotInConversation_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var nonParticipantId = _fixture.Create<ParticipantId>();
        var senderId = conversation.Participants.First();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        Action action = () => conversation.AddReaction(nonParticipantId, messageId, emoji);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AddReaction_ToNonExistentMessage_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var reactorId = conversation.Participants.First();
        var nonExistentMessageId = _fixture.Create<MessageId>();
        var emoji = "👍";

        // Act
        Action action = () => conversation.AddReaction(reactorId, nonExistentMessageId, emoji);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void RemoveReaction_WithValidParticipantAndMessage_ShouldRemoveReaction()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var senderId = participants.First();
        var reactorId = participants.Last();
        var emoji = "👍";
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;
        conversation.AddReaction(reactorId, messageId, emoji);

        // Act
        conversation.RemoveReaction(reactorId, messageId, emoji);

        // Assert
        conversation.Messages.First().Reactions.Should().BeEmpty();
    }

    [Test]
    public void Constructor_WithName_ShouldSetName()
    {
        // Arrange
        var name = _fixture.Create<string>();

        // Act
        var conversation = new ConversationBuilder().WithName(name).Build();

        // Assert
        conversation.Name.Should().Be(name);
    }

    [Test]
    public void ChangeName_ShouldUpdateConversationName()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var newName = _fixture.Create<string>();

        // Act
        conversation.ChangeName(newName);

        // Assert
        conversation.Name.Should().Be(newName);
    }

    [Test]
    public void MarkMessageAsRead_ShouldUpdateReceiptTimestamp()
    {
        // Arrange
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var senderId = participants.First();
        var readerId = participants.Last();
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        conversation.MarkMessageAsRead(readerId, messageId);

        // Assert
        var receipt = conversation.Messages.First().ReadReceipts.Single(r => r.ReaderId == readerId);
        receipt.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Test]
    public void MarkMessageAsRead_WhenParticipantIsNotInConversation_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var nonParticipantId = _fixture.Create<ParticipantId>();
        conversation.AddMessage(conversation.Participants.First(), "Hello");
        var messageId = conversation.Messages.First().Id;

        // Act
        Action action = () => conversation.MarkMessageAsRead(nonParticipantId, messageId);

        // Assert
        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void MarkMessageAsRead_ToNonExistentMessage_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new ConversationBuilder().Build();
        var readerId = conversation.Participants.First();
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
        var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        var conversation = new ConversationBuilder().WithParticipants(participants).Build();
        var senderId = participants.First();
        var readerId = participants.Last();
        conversation.AddMessage(senderId, "Hello");
        var messageId = conversation.Messages.First().Id;
        conversation.MarkMessageAsRead(readerId, messageId);
        var firstReadTime = conversation.Messages.First().ReadReceipts.Single(r => r.ReaderId == readerId).Timestamp;

        // Act
        conversation.MarkMessageAsRead(readerId, messageId);

        // Assert
        var receipt = conversation.Messages.First().ReadReceipts.Single(r => r.ReaderId == readerId);
        receipt.Timestamp.Should().Be(firstReadTime);
    }
}
