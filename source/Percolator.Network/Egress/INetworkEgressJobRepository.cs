using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

public interface INetworkEgressJobRepository
{
    Task SaveAsync(NetworkEgressJob job, CancellationToken cancellationToken = default);
    Task<NetworkEgressJob?> GetByIdAsync(EgressJobId jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NetworkEgressJob>> GetPendingJobsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task DeleteAsync(EgressJobId jobId, CancellationToken cancellationToken = default);
}
