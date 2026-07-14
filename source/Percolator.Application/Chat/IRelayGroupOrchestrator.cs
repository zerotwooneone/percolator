using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IRelayGroupOrchestrator
{
    Task<RelayGroupOperationStatus> PublishGroupRelayMessageAsync(
        ConversationId conversationId,
        uint requestedEpoch,
        ZkPresentationBytes presentation,
        CiphertextBytes ciphertext,
        CancellationToken cancellationToken);

    Task<RelayGroupOperationStatus> ModifyGroupAsync(
        ConversationId conversationId,
        uint baseEpoch,
        ZkPresentationBytes presentation,
        EncryptedGroupProfileBytes newEncryptedProfile,
        IReadOnlyList<Percolator.Identity.PublicIdentityId> addPublicIdentityIds,
        IReadOnlyList<Percolator.Identity.PublicIdentityId> removePublicIdentityIds,
        CancellationToken cancellationToken);

    Task<(RelayGroupOperationStatus Status, RelayGroupLedger? Ledger)> GetGroupStateAsync(
        ConversationId conversationId,
        ZkPresentationBytes presentation,
        CancellationToken cancellationToken);
}
