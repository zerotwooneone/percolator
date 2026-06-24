using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface IRelayGroupOrchestrator
{
    Task PublishGroupRelayMessageAsync(
        ConversationId conversationId,
        uint requestedEpoch,
        ZkPresentationBytes presentation,
        CiphertextBytes ciphertext,
        CancellationToken cancellationToken);
}
