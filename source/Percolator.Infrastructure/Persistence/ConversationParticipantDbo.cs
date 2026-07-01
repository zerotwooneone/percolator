using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Persistence;

public class ConversationParticipantDbo
{
    public ConversationId ConversationId { get; set; }
    public ChatPeerId ParticipantId { get; set; }

    public ConversationDbo Conversation { get; set; } = null!;
}
