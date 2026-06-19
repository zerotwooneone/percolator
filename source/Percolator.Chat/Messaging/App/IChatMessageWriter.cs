using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App;

public interface IChatMessageWriter
{
    Task AddTextMessageAsync(
        ConversationId conversationId,
        int selfIdentityId,
        ParticipantId senderId,
        string content,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddReadReceiptAsync(
        ConversationId conversationId,
        int selfIdentityId,
        ParticipantId readerId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddDeliveredReceiptAsync(
        ConversationId conversationId,
        int selfIdentityId,
        ParticipantId recipientId,
        MessageId messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken);

    Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        int selfIdentityId,
        ParticipantId reactorId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);
}
