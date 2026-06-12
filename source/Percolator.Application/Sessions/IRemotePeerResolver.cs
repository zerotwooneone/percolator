using Percolator.Cryptography;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Sessions;

public interface IRemotePeerResolver
{
    Task<IdentityPeerId> ResolveFromSession(SessionId sessionId);
}
