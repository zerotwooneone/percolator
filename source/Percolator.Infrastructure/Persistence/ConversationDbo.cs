using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Persistence;

public class ConversationDbo
{
    public ConversationId Id { get; set; }
    public string? Name { get; set; }
    public ChatSelfId SelfIdentityId { get; set; }
    public ConversationKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<MessageDbo> Messages { get; set; } = new List<MessageDbo>();
    public ICollection<ConversationParticipantDbo> Participants { get; set; } = new List<ConversationParticipantDbo>();
}
