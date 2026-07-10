using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.GroupLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken);
    Task ProvisionNewGroupAsync(ConversationId conversationId, RelayGroupPublicParamsBytes publicParams, IReadOnlyList<PublicIdentityId> memberPublicIdentityIds, CancellationToken cancellationToken);
    Task<bool> IsMemberAsync(ConversationId conversationId, PublicIdentityId publicIdentityId, CancellationToken cancellationToken);
}
