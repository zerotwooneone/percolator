namespace Percolator.Infrastructure.Persistence;

public sealed class EmojiReactionDbo
{
    public int Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid MessageGuid { get; set; }
    public Guid ReactorId { get; set; }
    public string Emoji { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
