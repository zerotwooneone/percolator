using MediatR;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class EmojiAnnotationReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SelfIdentityId { get; }
        public Guid ReactorPeerId { get; }
        public string Emoji { get; }
        public DateTimeOffset SentTimestampUtc { get; }

        public EmojiAnnotationReceivedEvent(
            Guid conversationId,
            Guid messageId,
            int selfIdentityId,
            Guid reactorPeerId,
            string emoji,
            DateTimeOffset sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            ReactorPeerId = reactorPeerId;
            Emoji = emoji ?? throw new ArgumentNullException(nameof(emoji));
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
