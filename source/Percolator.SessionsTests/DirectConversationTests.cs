using FluentAssertions;
using Percolator.Sessions;

namespace Percolator.SessionsTests;

[TestFixture]
public class DirectConversationTests
{
    private ConversationId _conversationId;
    private PeerId _peerId;
    private PeerId _senderId;

    [SetUp]
    public void Setup()
    {
        _conversationId = ConversationId.NewId();
        _peerId = PeerId.NewId();
        _senderId = _peerId;
    }

    [Test]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        // Act
        var conversation = new DirectConversation(_conversationId, _peerId);

        // Assert
        conversation.Id.Should().Be(_conversationId);
        conversation.PeerId.Should().Be(_peerId);
        conversation.State.Should().Be(ConversationState.Establishing);
    }

    [Test]
    public void Constructor_WithNullId_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new DirectConversation(null!, _peerId);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Constructor_WithNullPeerId_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new DirectConversation(_conversationId, null!);
        
        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void ValidateMessage_WithValidMessage_InValidState_DoesNotThrow()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _peerId);
        var message = new DirectMessage(MessageId.NewId(), _conversationId, _senderId, new OpaqueContent(new byte[1]));

        // Act
        Action act = () => conversation.ValidateMessage(message);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void ValidateMessage_InTerminatedState_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _peerId);
        conversation.Terminate();
        var message = new DirectMessage(MessageId.NewId(), _conversationId, _senderId, new OpaqueContent(new byte[1]));

        // Act
        Action act = () => conversation.ValidateMessage(message);
        
        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ValidateMessage_WithMismatchedConversationId_ThrowsArgumentException()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _peerId);
        var wrongConversationId = ConversationId.NewId();
        var message = new DirectMessage(MessageId.NewId(), wrongConversationId, _senderId, new OpaqueContent(new byte[1]));

        // Act
        Action act = () => conversation.ValidateMessage(message);
        
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Activate_FromEstablishingState_TransitionsToActive()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _peerId);
        
        // Act
        conversation.Activate();
        
        // Assert
        conversation.State.Should().Be(ConversationState.Active);
    }

    [Test]
    public void Activate_FromActiveState_ThrowsInvalidOperationException()
    {
        // Arrange
        var conversation = new DirectConversation(_conversationId, _peerId);
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
        var establishingConversation = new DirectConversation(ConversationId.NewId(), _peerId);
        var activeConversation = new DirectConversation(ConversationId.NewId(), _peerId);
        activeConversation.Activate();

        // Act
        establishingConversation.Terminate();
        activeConversation.Terminate();

        // Assert
        establishingConversation.State.Should().Be(ConversationState.Terminated);
        activeConversation.State.Should().Be(ConversationState.Terminated);
    }
}
