using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

/// <summary>
/// Repository for managing anonymous Relay egress jobs.
/// </summary>
public interface IAnonymousRelayEgressJobRepository
{
    Task<AnonymousRelayEgressJob?> GetByIdAsync(EgressJobId jobId, CancellationToken cancellationToken);
    Task AddAsync(AnonymousRelayEgressJob job, CancellationToken cancellationToken);
    Task UpdateAsync(AnonymousRelayEgressJob job, CancellationToken cancellationToken);
    Task DeleteAsync(EgressJobId jobId, CancellationToken cancellationToken);
}
