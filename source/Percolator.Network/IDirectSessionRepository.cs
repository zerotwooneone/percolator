using System;
using System.Threading.Tasks;

namespace Percolator.Network;

public interface IDirectSessionRepository
{
    Task<DirectSession?> GetBySessionIdAsync(DirectSessionId sessionId);
    Task<DirectSession?> GetByRemotePeerIdAsync(PeerId remotePeerId);
    Task UpsertAsync(PeerId remotePeerId, DirectSessionId sessionId);
    Task DeleteByRemotePeerIdAsync(PeerId remotePeerId);
}
