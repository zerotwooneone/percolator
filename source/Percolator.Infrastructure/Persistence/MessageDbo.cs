using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Persistence;

public class MessageDbo
{
    public int Id { get; set; }
    public ConversationId ConversationId { get; set; }

    // Domain MessageId for idempotency
    public PublicMessageId PublicMessageId { get; set; }

    public ChatPeerId? SenderPeerId { get; set; }
    public ChatSelfId? SenderSelfId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
