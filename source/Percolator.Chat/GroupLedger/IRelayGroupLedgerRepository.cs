using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Chat.GroupLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken);
    Task ProvisionNewGroupAsync(ConversationId conversationId, RelayGroupPublicParamsBytes publicParams, IReadOnlyList<PublicIdentityId> memberPublicIdentityIds, CancellationToken cancellationToken);
}
