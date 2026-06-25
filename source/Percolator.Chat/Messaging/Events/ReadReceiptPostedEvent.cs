using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class ReadReceiptPostedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public ChatPeerId SenderId { get; }
        public IReadOnlyList<ChatPeerId> RecipientIds { get; }
        public DateTime SentTimestampUtc { get; }

        public ReadReceiptPostedEvent(
            Guid conversationId,
            Guid messageId,
            ChatPeerId senderId,
            IReadOnlyList<ChatPeerId> recipientIds,
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
