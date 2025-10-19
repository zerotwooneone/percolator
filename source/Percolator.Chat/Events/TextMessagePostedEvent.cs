using System;
using System.Collections.Generic;
using MediatR;

namespace Percolator.Chat.Events
{
    public sealed class TextMessagePostedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public long SenderId { get; }
        public IReadOnlyList<long> RecipientIds { get; }
        public string Content { get; }
        public DateTime SentTimestampUtc { get; }

        public TextMessagePostedEvent(
            Guid conversationId,
            Guid messageId,
            long senderId,
            IReadOnlyList<long> recipientIds,
            string content,
            DateTime sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SenderId = senderId;
            RecipientIds = recipientIds ?? throw new ArgumentNullException(nameof(recipientIds));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
