using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Chat;

/// <summary>
/// Query interface for retrieving messages in a conversation.
/// </summary>
public interface IConversationMessageQueries
{
    /// <summary>
    /// Gets all messages for a conversation, ordered by timestamp.
    /// </summary>
    Task<List<MessageDto>> GetMessagesAsync(ConversationId conversationId, int selfIdentityId, CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO for a message.
/// </summary>
public sealed record MessageDto
{
    public Guid MessageId { get; init; }
    public Guid ConversationId { get; init; }
    public Guid SenderId { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}
