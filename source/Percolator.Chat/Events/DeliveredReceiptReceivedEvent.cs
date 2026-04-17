using MediatR;

namespace Percolator.Chat.Events
{
    public sealed class DeliveredReceiptReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SelfIdentityId { get; }
        public Guid RecipientPeerId { get; }
        public DateTimeOffset DeliveredTimestampUtc { get; }

        public DeliveredReceiptReceivedEvent(
            Guid conversationId,
            Guid messageId,
            int selfIdentityId,
            Guid recipientPeerId,
            DateTimeOffset deliveredTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            RecipientPeerId = recipientPeerId;
            DeliveredTimestampUtc = deliveredTimestampUtc;
        }
    }
}
