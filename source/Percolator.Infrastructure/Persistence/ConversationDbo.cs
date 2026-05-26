namespace Percolator.Infrastructure.Persistence;

public class ConversationDbo
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public int SelfIdentityId { get; set; }
    public ConversationKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MessageDbo> Messages { get; set; } = new List<MessageDbo>();
    public ICollection<ConversationParticipantDbo> Participants { get; set; } = new List<ConversationParticipantDbo>();
}
