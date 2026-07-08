namespace Percolator.Infrastructure.Persistence;

public class ConversationParticipantDbo
{
    public Guid ConversationId { get; set; }
    public uint ParticipantId { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
