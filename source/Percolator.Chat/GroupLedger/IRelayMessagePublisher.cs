using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

namespace Percolator.Chat.GroupLedger;

public interface IRelayMessagePublisher
{
    Task PublishAtomicAsync(RelayGroupLedger ledger, IReadOnlyList<ChatPeerId> recipients, QueuedPayloadBytes payload, CancellationToken cancellationToken);
}
