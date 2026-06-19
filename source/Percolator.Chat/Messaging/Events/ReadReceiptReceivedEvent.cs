using MediatR;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class ReadReceiptReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SelfIdentityId { get; }
        public Guid ReaderPeerId { get; }
        public DateTimeOffset SentTimestampUtc { get; }

        public ReadReceiptReceivedEvent(
            Guid conversationId,
            Guid messageId,
            int selfIdentityId,
            Guid readerPeerId,
            DateTimeOffset sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            ReaderPeerId = readerPeerId;
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
