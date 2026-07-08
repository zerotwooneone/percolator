using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;

namespace Percolator.Infrastructure.Persistence;

public class MessageDbo
{
    public int Id { get; set; }
    public Guid ConversationId { get; set; }

    // Domain MessageId for idempotency
    public PublicMessageId PublicMessageId { get; set; }

    public uint? SenderPeerId { get; set; }
    public uint? SenderSelfId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
