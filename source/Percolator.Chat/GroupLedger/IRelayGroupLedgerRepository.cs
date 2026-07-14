using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.GroupLedger;

public interface IRelayGroupLedgerRepository
{
    Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken);
    Task ProvisionNewGroupAsync(
        ConversationId conversationId,
        RelayGroupPublicParamsBytes publicParams,
        EncryptedGroupProfileBytes encryptedProfile,
        IReadOnlyList<ChatPeerId> memberPeerIds,
        CancellationToken cancellationToken);
    Task<bool> IsMemberAsync(ConversationId conversationId, ChatPeerId peerId, CancellationToken cancellationToken);
    Task UpdateGroupStateAsync(
        RelayGroupLedger ledger,
        IReadOnlyList<ChatPeerId> addPeerIds,
        IReadOnlyList<ChatPeerId> removePeerIds,
        CancellationToken cancellationToken);
}
