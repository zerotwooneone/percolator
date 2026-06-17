using Percolator.Contracts;

namespace Percolator.Application.Chat;

/// <summary>
/// Service for publishing messages to the Relay's encrypted group ledger.
/// </summary>
public interface IRelayGroupLedgerService
{
    /// <summary>
    /// Publishes a message to a group on the Relay's encrypted ledger.
    /// </summary>
    Task<PublishGroupMessageResponse> PublishAsync(PublishGroupMessageRequest request, CancellationToken ct = default);
}
