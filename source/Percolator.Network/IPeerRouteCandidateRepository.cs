namespace Percolator.Network;

public interface IPeerRouteCandidateRepository
{
    Task UpsertAsync(PeerRouteCandidate candidate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerRouteCandidate>> GetCandidatesAsync(int selfIdentityId, PeerId remotePeerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerRouteCandidate>> GetAllCandidatesAsync(int selfIdentityId, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}
