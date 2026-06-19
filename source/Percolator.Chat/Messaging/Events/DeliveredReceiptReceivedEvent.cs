using MediatR;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class DeliveredReceiptReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SelfIdentityId { get; }
        public Guid RecipientPeerId { get; }
        public DateTimeOffset DeliveredTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }

        public DeliveredReceiptReceivedEvent(
            Guid conversationId,
            Guid messageId,
            int selfIdentityId,
            Guid recipientPeerId,
            DateTimeOffset deliveredTimestampUtc,
            DirectSessionIdValueObject? directSessionId = null)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            RecipientPeerId = recipientPeerId;
            DeliveredTimestampUtc = deliveredTimestampUtc;
            DirectSessionId = directSessionId;
        }
    }
}
