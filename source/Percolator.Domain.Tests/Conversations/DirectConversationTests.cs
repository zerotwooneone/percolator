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
    public void AppendMessage_AppendsMessage_AndUpdatesLastActivityTimestamp()
    {
        var conversation = new DirectConversation(_conversationId, _ownerId, _peerId, _timeProvider.UtcNow);

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
    public void MarkAsRead_UpdatesLastReadMessageId()
    {
        var conversation = new DirectConversation(_conversationId, _ownerId, _peerId, _timeProvider.UtcNow);
        var msgId = MessageId.New();

        conversation.MarkAsRead(msgId);

        conversation.LastReadMessageId.Should().Be(msgId);
    }
}
