using System;
using System.Threading.Tasks;

namespace Percolator.Network;

public interface IDirectSessionRepository
{
    Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId, int selfIdentityId);
    Task<DirectSession?> GetByRemotePeerIdAsync(PeerId remotePeerId, int selfIdentityId);
    Task UpsertAsync(PeerId remotePeerId, DirectSessionId sessionId, int selfIdentityId);
    Task DeleteByRemotePeerIdAsync(PeerId remotePeerId, int selfIdentityId);
}
