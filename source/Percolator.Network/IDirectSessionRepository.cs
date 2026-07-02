namespace Percolator.Network;

public interface IDirectSessionRepository
{
    Task<IReadOnlyList<DirectSession>> ListAsync(uint selfIdentityId);
    Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId, uint selfIdentityId);
    Task<DirectSession?> GetByRemotePeerIdAsync(PeerId remotePeerId, uint selfIdentityId);
    Task UpsertAsync(PeerId remotePeerId, DirectSessionId sessionId, uint selfIdentityId);
    Task DeleteByRemotePeerIdAsync(PeerId remotePeerId, uint selfIdentityId);
}
