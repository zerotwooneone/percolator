using Percolator.Chat.GroupMembership;

namespace Percolator.Infrastructure.Persistence;

public sealed class DeliveredReceiptDbo
{
    public int Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageGuid { get; set; }
    public ChatPeerId RecipientId { get; set; }
    public DateTimeOffset DeliveredAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
