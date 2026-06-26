using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat;

/// <summary>
/// Repository for managing group conversations.
/// </summary>
public interface IGroupConversationRepository
{
    Task<GroupConversation?> GetByIdAsync(ConversationId id, uint selfIdentityId, CancellationToken cancellationToken);
    Task AddAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken cancellationToken);
    Task UpdateAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken cancellationToken);
    /// <summary>
    /// Adds a group conversation atomically with outbox events for group provisioning.
    /// This method saves the group state, members, and domain events in a single transaction.
    /// </summary>
    Task AddWithOutboxAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken cancellationToken);
}
