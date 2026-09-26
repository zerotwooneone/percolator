using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography.GroupLedger;

namespace Percolator.Application.Chat;

public interface IRelayGroupOrchestrator
{
    Task<RelayGroupOperationStatus> ProcessAnonymousGroupRequestAsync(
        ConversationId conversationId,
        uint senderKeyId,
        ZkPresentationBytes presentation,
        IReadOnlyList<Percolator.Identity.PublicIdentityId> targetIdentities,
        CiphertextBytes? ciphertext,
        EncryptedGroupProfileBytes? newEncryptedEntries,
        uint? newEpoch,
        CancellationToken cancellationToken);

    Task<(RelayGroupOperationStatus Status, RelayGroupStateDto? State)> GetGroupStateAsync(
        ConversationId conversationId,
        ZkPresentationBytes presentation,
        CancellationToken cancellationToken);
}
