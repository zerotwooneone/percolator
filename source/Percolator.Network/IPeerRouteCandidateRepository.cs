namespace Percolator.Network;

public interface IPeerRouteCandidateRepository
{
    Task UpsertAsync(PeerRouteCandidate candidate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerRouteCandidate>> GetCandidatesAsync(uint selfIdentityId, PeerId remotePeerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerRouteCandidate>> GetAllCandidatesAsync(uint selfIdentityId, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
    Task PruneAsync(int selfIdentityId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
