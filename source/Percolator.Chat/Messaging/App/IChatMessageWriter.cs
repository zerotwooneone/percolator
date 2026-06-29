using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App;

public interface IChatMessageWriter
{
    Task AddTextMessageAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId senderId,
        string content,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddReadReceiptAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId readerId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddDeliveredReceiptAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId recipientId,
        MessageId messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken);

    Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId reactorId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);
}
