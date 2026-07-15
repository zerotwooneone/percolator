using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Chat;

/// <summary>
/// Query interface for retrieving group conversation information.
/// </summary>
public interface IGroupConversationQueries
{
    /// <summary>
    /// Gets all active group conversations for the specified self identity.
    /// </summary>
    Task<IReadOnlyList<GroupConversationDto>> ListActiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO for group conversation query results.
/// </summary>
public sealed record GroupConversationDto
{
    public ConversationId ConversationId { get; init; }
    public ChatPeerId RelayPeerId { get; init; }
    public string? Name { get; init; }
    public ChatSelfId SelfIdentityId { get; init; }
}
