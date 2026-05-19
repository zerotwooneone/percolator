using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Chat.App;

/// <summary>
/// Application-facing persistence accessor for group cryptographic state.
///
/// The stored blob is the raw 32-byte <see cref="GroupMasterKey"/>.
/// At-rest protection is provided by the infrastructure's encrypted SQLite database.
/// </summary>
public interface IGroupCryptoStateRepository
{
    /// <summary>
    /// Loads the 32-byte GroupMasterKey for a group conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID used as the primary key for crypto state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The GroupMasterKey, or null when no state exists yet.</returns>
    Task<GroupMasterKey?> GetGroupMasterKeyAsync(ConversationId conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the 32-byte GroupMasterKey for a group conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID used as the primary key for crypto state.</param>
    /// <param name="groupMasterKey">The GroupMasterKey to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertGroupMasterKeyAsync(ConversationId conversationId, GroupMasterKey groupMasterKey, CancellationToken cancellationToken = default);
}
