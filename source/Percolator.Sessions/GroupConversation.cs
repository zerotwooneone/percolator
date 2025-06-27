namespace Percolator.Sessions;

/// <summary>
/// Represents a stateless identifier for a group session.
/// </summary>
public record GroupConversation
{
    public ConversationId Id { get; }

    public GroupConversation(ConversationId id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }
}
