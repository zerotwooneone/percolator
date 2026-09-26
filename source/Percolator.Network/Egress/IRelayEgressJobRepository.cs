using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

public interface IRelayEgressJobRepository
{
    Task<RelayEgressJob?> GetByIdAsync(Guid jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RelayEgressJob>> GetByDestinationPeerIdAsync(NetworkPeerId destinationPeerId, CancellationToken cancellationToken);
    Task SaveAsync(RelayEgressJob job, CancellationToken cancellationToken);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);
}
