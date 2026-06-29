using MediatR;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class TextMessageReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public uint SelfIdentityId { get; }
        public ChatPeerId SenderPeerId { get; }
        public string Content { get; }
        public DateTimeOffset SentTimestampUtc { get; }
        public DirectSessionIdValueObject? DirectSessionId { get; }

        public TextMessageReceivedEvent(
            Guid conversationId,
            Guid messageId,
            uint selfIdentityId,
            ChatPeerId senderPeerId,
            string content,
            DateTimeOffset sentTimestampUtc,
            DirectSessionIdValueObject? directSessionId = null)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            SenderPeerId = senderPeerId;
            Content = content ?? throw new ArgumentNullException(nameof(content));
            SentTimestampUtc = sentTimestampUtc;
            DirectSessionId = directSessionId;
        }
    }
}
