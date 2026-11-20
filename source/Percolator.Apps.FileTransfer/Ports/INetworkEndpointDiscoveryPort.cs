using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Ports;

public interface INetworkEndpointDiscoveryPort
{
    ValueTask<IReadOnlyList<DirectEndpointCandidate>> DiscoverLocalCandidatesAsync(
        DirectWanPolicy wanPolicy,
        CancellationToken ct = default);

    ValueTask<RelayRendezvousDescriptor?> AllocateRelayRendezvousAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default);
}
