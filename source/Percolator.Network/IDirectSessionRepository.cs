namespace Percolator.Network;

public interface IDirectSessionRepository
{
    Task<IReadOnlyList<DirectSession>> ListAsync(NetworkSelfId selfIdentityId);
    Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId, NetworkSelfId selfIdentityId);
    Task<DirectSession?> GetByRemotePeerIdAsync(PeerId remotePeerId, NetworkSelfId selfIdentityId);
    Task UpsertAsync(PeerId remotePeerId, DirectSessionId sessionId, NetworkSelfId selfIdentityId);
    Task DeleteByRemotePeerIdAsync(PeerId remotePeerId, NetworkSelfId selfIdentityId);
}
