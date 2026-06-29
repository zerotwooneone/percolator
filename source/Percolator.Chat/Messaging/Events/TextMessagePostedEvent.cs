using MediatR;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

namespace Percolator.Chat.Messaging.Events
{
    [Obsolete("delete this. use DispatchTextMessageCommand instead")]
    public sealed class TextMessagePostedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public uint SenderSelfIdentityId { get; }
        public IReadOnlyList<ChatPeerId> RecipientPeerIds { get; }
        public string Content { get; }
        public DateTimeOffset SentTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }

        public TextMessagePostedEvent(
            Guid conversationId,
            Guid messageId,
            uint senderSelfIdentityId,
            IReadOnlyList<ChatPeerId> recipientPeerIds,
            string content,
            DateTimeOffset sentTimestampUtc,
            DirectSessionIdValueObject? directSessionId = null)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SenderSelfIdentityId = senderSelfIdentityId;
            RecipientPeerIds = recipientPeerIds ?? throw new ArgumentNullException(nameof(recipientPeerIds));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
            DirectSessionId = directSessionId;
        }
    }
}
