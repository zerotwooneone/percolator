using FluentAssertions;
using NUnit.Framework;
using System;
using Percolator.Sessions;

namespace Percolator.SessionsTests;

[TestFixture]
public class DirectConversationTests
{
    private ConversationId _conversationId;
    private PeerId _localPeerId;
    private PeerId _remotePeerId;

    [SetUp]
    public void SetUp()
    {
        _conversationId = ConversationId.NewId();
        _localPeerId = PeerId.NewId();
        _remotePeerId = PeerId.NewId();
    }

    [Test]
    public void Constructor_WithValidArguments_CreatesInstance()
    {
        // Act
        var conversation = new DirectConversation(_conversationId, _localPeerId, _remotePeerId);

        // Assert
        conversation.Id.Should().Be(_conversationId);
        conversation.LocalPeerId.Should().Be(_localPeerId);
        conversation.RemotePeerId.Should().Be(_remotePeerId);
        conversation.State.Should().Be(ConversationState.Establishing);
    }

    [Test]
    public void Constructor_WithNullId_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new DirectConversation(null!, _localPeerId, _remotePeerId);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithNullLocalPeerId_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new DirectConversation(_conversationId, null!, _remotePeerId);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithNullRemotePeerId_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new DirectConversation(_conversationId, _localPeerId, null!);
        
        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ValidateMessage_WithValidMessage_InValidState_DoesNotThrow()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _localPeerId, _remotePeerId);
        var message = new DirectMessage(MessageId.NewId(), _conversationId, _localPeerId, new OpaqueContent(new byte[1]));

        // Act
        Action act = () => conversation.ValidateMessage(message);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void ValidateMessage_InTerminatedState_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _localPeerId, _remotePeerId);
        conversation.Terminate();
        var message = new DirectMessage(MessageId.NewId(), _conversationId, _localPeerId, new OpaqueContent(new byte[1]));

        // Act
        Action act = () => conversation.ValidateMessage(message);

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ValidateMessage_WithMismatchedConversationId_ThrowsArgumentException()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _localPeerId, _remotePeerId);
        var wrongConversationId = ConversationId.NewId();
        var message = new DirectMessage(MessageId.NewId(), wrongConversationId, _localPeerId, new OpaqueContent(new byte[1]));

        // Act
        Action act = () => conversation.ValidateMessage(message);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Activate_FromEstablishingState_TransitionsToActive()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _localPeerId, _remotePeerId);
        
        // Act
        conversation.Activate();

        // Assert
        conversation.State.Should().Be(ConversationState.Active);
    }

    [Test]
    public void Activate_FromActiveState_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _localPeerId, _remotePeerId);
        conversation.Activate();
        
        // Act
        Action act = () => conversation.Activate();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Terminate_FromAnyState_TransitionsToTerminated()
    {
        // Arrange
        var establishingConversation = new DirectConversation(ConversationId.NewId(), _localPeerId, _remotePeerId);
        var activeConversation = new DirectConversation(ConversationId.NewId(), _localPeerId, _remotePeerId);
        activeConversation.Activate();

        // Act
        establishingConversation.Terminate();
        activeConversation.Terminate();

        // Assert
        establishingConversation.State.Should().Be(ConversationState.Terminated);
        activeConversation.State.Should().Be(ConversationState.Terminated);
    }
}
