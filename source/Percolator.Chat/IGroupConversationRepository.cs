using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

/// <summary>
/// Repository for managing group conversations.
/// </summary>
public interface IGroupConversationRepository
{
    Task<GroupConversation?> GetByIdAsync(ConversationId id, int selfIdentityId, CancellationToken cancellationToken);
    Task AddAsync(GroupConversation conversation, int selfIdentityId, CancellationToken cancellationToken);
    Task UpdateAsync(GroupConversation conversation, int selfIdentityId, CancellationToken cancellationToken);
}
