using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Delivery.Ports;

public interface IRelayMailboxRepository
{
    Task<RelayMailboxQueue?> GetByRelayIdAsync(PublicIdentityId relayIdentityId, CancellationToken cancellationToken = default);
    Task SaveAsync(RelayMailboxQueue queue, CancellationToken cancellationToken = default);
}
