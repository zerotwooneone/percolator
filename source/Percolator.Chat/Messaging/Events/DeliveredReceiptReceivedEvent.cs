using MediatR;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

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
