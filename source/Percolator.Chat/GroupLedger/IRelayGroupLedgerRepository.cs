using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.GroupLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken);
}
