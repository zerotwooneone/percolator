using System;

namespace Percolator.Infrastructure.Persistence;

public class MessageDbo
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public int SelfIdentityId { get; set; }

    public Guid SenderId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
