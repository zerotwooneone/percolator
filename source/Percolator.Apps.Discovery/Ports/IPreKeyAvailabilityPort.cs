using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Apps.Discovery.Ports;

/// <summary>
/// Port for checking whether a mailbox has an active, unexpired Signed PreKey (SPK).
/// Keyed by the peer's advertised <see cref="BlindedRoutingToken"/> — the directory
/// never resolves the owner's public identity.
/// </summary>
public interface IPreKeyAvailabilityPort
{
    ValueTask<bool> HasActiveSignedPreKeyAsync(
        BlindedRoutingToken routingToken,
        CancellationToken ct = default);
}
