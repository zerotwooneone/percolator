using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App;

public interface IChatMessageWriter
{
    Task AddTextMessageAsync(
        ConversationId conversationId,
        ChatSelfId selfIdentityId,
        ChatPeerId senderId,
        string content,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddReadReceiptAsync(
        ConversationId conversationId,
        ChatSelfId selfIdentityId,
        ChatPeerId readerId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddDeliveredReceiptAsync(
        ConversationId conversationId,
        ChatSelfId selfIdentityId,
        ChatPeerId recipientId,
        MessageId messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken);

    Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        ChatSelfId selfIdentityId,
        ChatPeerId reactorId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);
}
