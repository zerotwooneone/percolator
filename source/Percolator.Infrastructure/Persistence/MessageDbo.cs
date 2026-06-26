using Percolator.Chat.GroupMembership;

namespace Percolator.Infrastructure.Persistence;

public class MessageDbo
{
    public int Id { get; set; }
    public Guid ConversationId { get; set; }

    // Domain MessageId for idempotency
    public Guid MessageGuid { get; set; }

    public ChatPeerId SenderId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
