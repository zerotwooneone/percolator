using Percolator.Domain.Conversations.Events;
using Percolator.Domain.Conversations.Model;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Conversations;

[TestFixture]
public class DirectConversationTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private PublicIdentityId _ownerId;
    private PublicIdentityId _peerId;
    private ConversationId _conversationId;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _ownerId = PublicIdentityId.New();
        _peerId = PublicIdentityId.New();
        _conversationId = ConversationId.New();
    }

    [Test]
    public void Create_WithValidParameters_ReturnsSuccess()
    {
        var result = DirectConversation.Create(_conversationId, _ownerId, _peerId, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(_conversationId);
    }

    [Test]
    public void Create_WithSelfAsPeer_ReturnsSelfConversationNotAllowedError()
    {
        var result = DirectConversation.Create(_conversationId, _ownerId, _ownerId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SELF_CONVERSATION_NOT_ALLOWED");
    }

    [Test]
    public void AppendMessage_AppendsMessage_AndUpdatesLastActivityTimestamp()
    {
        var conversation = DirectConversation.Create(_conversationId, _ownerId, _peerId, _timeProvider).Value;

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var message = new Message(
            MessageId.New(),
            conversation.Id,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 42 },
            _timeProvider.UtcNow);

        var result = conversation.AppendMessage(message, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        conversation.LastActivityUtc.Should().Be(_timeProvider.UtcNow);
        conversation.DomainEvents.Should().ContainSingle(e => e is MessageAppendedEvent);
    }

    [Test]
    public void AppendMessage_WithMismatchedConversationId_ReturnsError()
    {
        var conversation = DirectConversation.Create(_conversationId, _ownerId, _peerId, _timeProvider).Value;
        var wrongConvId = ConversationId.New();

        var message = new Message(
            MessageId.New(),
            wrongConvId,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 42 },
            _timeProvider.UtcNow);

        var result = conversation.AppendMessage(message, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CONVERSATION_MISMATCH");
    }

    [Test]
    public void AppendMessage_WithNonParticipantSender_ReturnsSenderNotParticipantError()
    {
        var conversation = DirectConversation.Create(_conversationId, _ownerId, _peerId, _timeProvider).Value;
        var strangerId = PublicIdentityId.New();

        var message = new Message(
            MessageId.New(),
            conversation.Id,
            strangerId,
            DeviceId.Primary,
            new byte[] { 42 },
            _timeProvider.UtcNow);

        var result = conversation.AppendMessage(message, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SENDER_NOT_PARTICIPANT");
    }

    [Test]
    public void MarkAsRead_UpdatesLastReadMessageId()
    {
        var conversation = DirectConversation.Create(_conversationId, _ownerId, _peerId, _timeProvider).Value;
        var msgId = MessageId.New();

        conversation.MarkAsRead(msgId);

        conversation.LastReadMessageId.Should().Be(msgId);
    }
}
