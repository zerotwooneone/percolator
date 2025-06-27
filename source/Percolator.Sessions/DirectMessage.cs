namespace Percolator.Sessions;

/// <summary>
/// Represents a single piece of data exchanged within a direct conversation.
/// </summary>
public record DirectMessage
{
    /// <summary>
    /// The unique identifier for the message.
    /// </summary>
    public MessageId Id { get; }

    /// <summary>
    /// The ID of the conversation this message belongs to.
    /// </summary>
    public ConversationId ConversationId { get; }

    /// <summary>
    /// The ID of the peer who sent the message.
    /// </summary>
    public PeerId SenderId { get; }

    /// <summary>
    /// The opaque content of the message.
    /// </summary>
    public OpaqueContent Content { get; }

    /// <summary>
    /// The UTC timestamp of when the message was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    public DirectMessage(MessageId id, ConversationId conversationId, PeerId senderId, OpaqueContent content)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ConversationId = conversationId ?? throw new ArgumentNullException(nameof(conversationId));
        SenderId = senderId ?? throw new ArgumentNullException(nameof(senderId));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Timestamp = DateTimeOffset.UtcNow;
    }
}
