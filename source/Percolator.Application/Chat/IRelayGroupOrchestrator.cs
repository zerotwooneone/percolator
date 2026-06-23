using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;

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
