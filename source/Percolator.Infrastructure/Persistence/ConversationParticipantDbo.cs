namespace Percolator.Infrastructure.Persistence;

public class ConversationParticipantDbo
{
    public Guid ConversationId { get; set; }
    public Guid ParticipantId { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
