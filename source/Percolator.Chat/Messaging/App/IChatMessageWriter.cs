using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App;

public interface IChatMessageWriter
{
    Task AddTextMessageAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ParticipantId senderId,
        string content,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddReadReceiptAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ParticipantId readerId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddDeliveredReceiptAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ParticipantId recipientId,
        MessageId messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken);

    Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ParticipantId reactorId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);
}
