using System;

namespace Percolator.Infrastructure.Persistence;

public sealed class ReadReceiptDbo
{
    public int Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageGuid { get; set; }
    public Guid ReaderId { get; set; }
    public DateTimeOffset SentAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
