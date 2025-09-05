using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App;

public interface IChatMessageWriter
{
    Task AddTextMessageAsync(
        ConversationId conversationId,
        int selfIdentityId,
        string content,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddReadReceiptAsync(
        ConversationId conversationId,
        int selfIdentityId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        int selfIdentityId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);
}
