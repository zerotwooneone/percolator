using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Ports;

public interface IRelayLedgerRepository
{
    Task<RelayGroupLedger?> GetByChannelIdAsync(ChannelId channelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelayGroupLedger>> GetAllForRelayAsync(PublicIdentityId relayIdentityId, CancellationToken cancellationToken = default);
    Task SaveAsync(RelayGroupLedger ledger, CancellationToken cancellationToken = default);
}
