using MediatR;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Events
{
    public sealed class TextMessagePostedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public int SenderSelfIdentityId { get; }
        public IReadOnlyList<Guid> RecipientPeerIds { get; }
        public string Content { get; }
        public DateTimeOffset SentTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }
        public Guid? GroupConversationGuid { get; }
        public byte[]? AuthorIdentityKey { get; }

        public TextMessagePostedEvent(
            Guid conversationId,
            Guid messageId,
            int senderSelfIdentityId,
            IReadOnlyList<Guid> recipientPeerIds,
            string content,
            DateTimeOffset sentTimestampUtc,
            DirectSessionIdValueObject? directSessionId = null,
            Guid? groupConversationGuid = null,
            byte[]? authorIdentityKey = null)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SenderSelfIdentityId = senderSelfIdentityId;
            RecipientPeerIds = recipientPeerIds ?? throw new ArgumentNullException(nameof(recipientPeerIds));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
            DirectSessionId = directSessionId;
            GroupConversationGuid = groupConversationGuid;
            AuthorIdentityKey = authorIdentityKey;
        }
    }
}
