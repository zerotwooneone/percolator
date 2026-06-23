using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.GroupLedger;

public interface IRelayMessagePublisher
{
    Task PublishAtomicAsync(RelayGroupLedger ledger, IReadOnlyList<ChatPeerId> recipients, QueuedPayloadBytes payload, CancellationToken cancellationToken);
}
