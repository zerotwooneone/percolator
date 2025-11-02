using System;
using System.Collections.Generic;
using MediatR;

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

        public TextMessagePostedEvent(
            Guid conversationId,
            Guid messageId,
            int senderSelfIdentityId,
            IReadOnlyList<Guid> recipientPeerIds,
            string content,
            DateTimeOffset sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SenderSelfIdentityId = senderSelfIdentityId;
            RecipientPeerIds = recipientPeerIds ?? throw new ArgumentNullException(nameof(recipientPeerIds));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
