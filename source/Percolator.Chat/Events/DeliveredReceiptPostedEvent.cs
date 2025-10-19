using MediatR;

namespace Percolator.Chat.Events
{
    public sealed class DeliveredReceiptPostedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public long SenderId { get; }
        public IReadOnlyList<long> RecipientIds { get; }
        public DateTime SentTimestampUtc { get; }

        public DeliveredReceiptPostedEvent(
            Guid conversationId,
            Guid messageId,
            long senderId,
            IReadOnlyList<long> recipientIds,
            DateTime sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SenderId = senderId;
            RecipientIds = recipientIds ?? throw new ArgumentNullException(nameof(recipientIds));
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
