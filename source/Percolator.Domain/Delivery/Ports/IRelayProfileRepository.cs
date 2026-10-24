using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Domain.Delivery.Ports;

/// <summary>
/// Persistence port for storing and querying relay-hosted encrypted profile slots.
/// </summary>
public interface IRelayProfileRepository
{
    ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default);
    ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default);
    ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default);
}
