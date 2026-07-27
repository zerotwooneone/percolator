using Percolator.Network.ValueObjects;

namespace Percolator.Network.Egress;

/// <summary>
/// Repository for managing authenticated peer egress jobs.
/// </summary>
public interface IAuthenticatedPeerEgressJobRepository
{
    Task<AuthenticatedPeerEgressJob?> GetByIdAsync(EgressJobId jobId, CancellationToken cancellationToken);
    Task AddAsync(AuthenticatedPeerEgressJob job, CancellationToken cancellationToken);
    Task UpdateAsync(AuthenticatedPeerEgressJob job, CancellationToken cancellationToken);
    Task DeleteAsync(EgressJobId jobId, CancellationToken cancellationToken);
}
