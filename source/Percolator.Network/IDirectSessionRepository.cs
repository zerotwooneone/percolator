namespace Percolator.Network;

public interface IDirectSessionRepository
{
    Task<IReadOnlyList<DirectSession>> ListAsync(NetworkSelfId selfIdentityId);
    Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId, NetworkSelfId selfIdentityId);
    Task<DirectSession?> GetByRemotePeerIdAsync(NetworkPeerId remoteNetworkPeerId, NetworkSelfId selfIdentityId);
    Task UpsertAsync(NetworkPeerId remoteNetworkPeerId, DirectSessionId sessionId, NetworkSelfId selfIdentityId);
    Task DeleteByRemotePeerIdAsync(NetworkPeerId remoteNetworkPeerId, NetworkSelfId selfIdentityId);
}
