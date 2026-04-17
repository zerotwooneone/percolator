using MediatR;

namespace Percolator.Chat.Events
{
    public sealed class TextMessageReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SelfIdentityId { get; }
        public Guid SenderPeerId { get; }
        public string Content { get; }
        public DateTimeOffset SentTimestampUtc { get; }

        public TextMessageReceivedEvent(
            Guid conversationId,
            Guid messageId,
            int selfIdentityId,
            Guid senderPeerId,
            string content,
            DateTimeOffset sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            SenderPeerId = senderPeerId;
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
