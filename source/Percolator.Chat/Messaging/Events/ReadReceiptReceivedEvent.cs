using MediatR;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Messaging.Events
{
    public sealed class ReadReceiptReceivedEvent : INotification
    {
        public Guid ConversationId { get; }
        public Guid MessageId { get; }
        public uint SelfIdentityId { get; }
        public ChatPeerId ReaderPeerId { get; }
        public DateTimeOffset SentTimestampUtc { get; }

        public ReadReceiptReceivedEvent(
            Guid conversationId,
            Guid messageId,
            uint selfIdentityId,
            ChatPeerId readerPeerId,
            DateTimeOffset sentTimestampUtc)
        {
            ConversationId = conversationId;
            MessageId = messageId;
            SelfIdentityId = selfIdentityId;
            ReaderPeerId = readerPeerId;
            SentTimestampUtc = sentTimestampUtc;
        }
    }
}
