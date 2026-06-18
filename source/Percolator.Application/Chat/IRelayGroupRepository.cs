using Percolator.Chat;

namespace Percolator.Application.Chat;

/// <summary>
/// Repository for managing the Relay's encrypted group ledger.
/// </summary>
public interface IRelayGroupRepository
{
    /// <summary>
    /// Retrieves the ledger for a given conversation.
    /// </summary>
    Task<RelayGroupLedger?> GetLedgerAsync(ConversationId conversationId, CancellationToken ct = default);

    /// <summary>
    /// Saves the ledger state.
    /// </summary>
    Task SaveAsync(RelayGroupLedger ledger, CancellationToken ct = default);
}
