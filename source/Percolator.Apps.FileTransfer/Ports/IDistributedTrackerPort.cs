using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Apps.FileTransfer.Ports;

public interface IDistributedTrackerPort
{
    ValueTask<SwarmAnnounceResult> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        SwarmPeerRole role,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default);

    ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default);
}
