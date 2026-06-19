using MediatR;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class EmojiAnnotationPostedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public string Emoji { get; }
        public long SenderId { get; }
        public IReadOnlyList<long> RecipientIds { get; }
        public DateTime SentTimestampUtc { get; }

        public EmojiAnnotationPostedEvent(
            Guid conversationId,
            Guid messageId,
            string emoji,
            long senderId,
            IReadOnlyList<long> recipientIds,
            DateTime sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            Emoji = emoji;
            SenderId = senderId;
            RecipientIds = recipientIds ?? throw new ArgumentNullException(nameof(recipientIds));
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
