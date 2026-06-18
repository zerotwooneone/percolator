using Percolator.Chat;
using Percolator.Chat.ValueObjects;

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
    /// Saves an existing ledger state (for epoch advancements).
    /// </summary>
    Task SaveAsync(RelayGroupLedger ledger, CancellationToken ct = default);

    /// <summary>
    /// Provisions a new ledger and its initial blinded roster in a single atomic transaction.
    /// Uses raw byte arrays for the roster to avoid cross-domain coupling.
    /// </summary>
    Task ProvisionAsync(RelayGroupLedger ledger, IReadOnlyList<byte[]> initialRoster, CancellationToken ct = default);
}
