using ChatConversationId = Percolator.Chat.Messaging.ValueObjects.ConversationId;

namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Application-facing persistence accessor for group cryptographic state.
///
/// The stored blob is the raw 32-byte GroupMasterKeyBytes.
/// At-rest protection is provided by the infrastructure's encrypted SQLite database.
/// </summary>
public interface IGroupCryptoStateRepository
{
    /// <summary>
    /// Loads the 32-byte GroupMasterKeyBytes for a group conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID used as the primary key for crypto state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The GroupMasterKeyBytes, or null when no state exists yet.</returns>
    Task<GroupMasterKeyBytes?> GetGroupMasterKeyAsync(ChatConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the 32-byte GroupMasterKeyBytes for a group conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID used as the primary key for crypto state.</param>
    /// <param name="groupMasterKey">The GroupMasterKeyBytes to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertGroupMasterKeyAsync(ChatConversationId conversationId, GroupMasterKeyBytes groupMasterKey, CancellationToken cancellationToken = default);
}
