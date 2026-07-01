using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App;

public interface IChatMessageWriter
{
    Task AddTextMessageAsync(
        ConversationId conversationId,
        ParticipantId participantId,
        string content,
        PublicMessageId publicMessageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    Task AddReadReceiptAsync(
        ConversationId conversationId,
        ChatSelfId selfIdentityId,
        ChatPeerId readerId,
        PublicMessageId publicMessageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken);

    
}
