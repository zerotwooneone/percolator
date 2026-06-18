using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Chat;

/// <summary>
/// Queries for reading the blinded roster associated with a relay group.
/// </summary>
public interface IRelayBlindedRosterQueries
{
    /// <summary>
    /// Retrieves the list of blinded routing tokens (public key hashes) for a given conversation.
    /// Returns raw byte arrays to maintain domain boundary isolation from the Identity domain.
    /// </summary>
    Task<IReadOnlyList<byte[]>> GetBlindedRosterAsync(ConversationId conversationId, CancellationToken ct = default);
}
