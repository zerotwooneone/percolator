using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Ports;

public interface IDistributedTrackerPort
{
    ValueTask<IReadOnlyList<DirectEndpointCandidate>> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        SwarmPeerRole role,
        CancellationToken ct = default);

    ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        CancellationToken ct = default);
}
