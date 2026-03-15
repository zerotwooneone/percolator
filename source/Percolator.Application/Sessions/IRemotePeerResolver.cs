using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Sessions;

public interface IRemotePeerResolver
{
    Task<PeerId> ResolveFromSession(SessionId sessionId);
}
