using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class TextMessageReceivedEvent : INotification
    {
        public ConversationId ConversationId { get; }
        public PublicMessageId MessageId { get; }
        public ChatSelfId SelfIdentityId { get; }
        public ParticipantId SenderId { get; }
        public string Content { get; }
        public DateTimeOffset SentTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }

        public TextMessageReceivedEvent(
            ConversationId conversationId,
            PublicMessageId messageId,
            ChatSelfId selfIdentityId,
            ParticipantId senderId,
            string content,
            DateTimeOffset sentTimestampUtc,
            DirectSessionIdValueObject? directSessionId = null)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            SenderId = senderId;
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
            DirectSessionId = directSessionId;
        }
    }
}
