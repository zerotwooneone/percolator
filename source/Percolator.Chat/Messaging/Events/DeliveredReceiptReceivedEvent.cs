using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class DeliveredReceiptReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public uint SelfIdentityId { get; }
        public ChatPeerId RecipientPeerId { get; }
        public DateTimeOffset DeliveredTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }

        public DeliveredReceiptReceivedEvent(
            Guid conversationId,
            Guid messageId,
            uint selfIdentityId,
            ChatPeerId recipientPeerId,
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
