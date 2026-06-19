using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class TextMessageReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SelfIdentityId { get; }
        public Guid SenderPeerId { get; }
        public string Content { get; }
        public DateTimeOffset SentTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }

        public TextMessageReceivedEvent(
            Guid conversationId,
            Guid messageId,
            int selfIdentityId,
            Guid senderPeerId,
            string content,
            DateTimeOffset sentTimestampUtc,
            DirectSessionIdValueObject? directSessionId = null)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            SenderPeerId = senderPeerId;
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
            DirectSessionId = directSessionId;
        }
    }
}
