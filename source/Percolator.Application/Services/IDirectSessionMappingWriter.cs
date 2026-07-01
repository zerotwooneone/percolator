using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Services;

/// <summary>
/// Service responsible for persisting DirectSession mappings between (SelfIdentityId, RemotePeerId) and SessionId.
/// This provides a centralized location for maintaining the DirectSession invariant across all handshake paths.
/// </summary>
public interface IDirectSessionMappingWriter
{
    /// <summary>
    /// Persists a DirectSession mapping between the remote peer and the session ID.
    /// Uses best-effort semantics: failures are logged but do not throw exceptions.
    /// </summary>
    /// <param name="remotePeerId">The remote peer identifier.</param>
    /// <param name="sessionId">The cryptographic session ID.</param>
    /// <param name="selfIdentityId">The local self-identity ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteMappingAsync(PeerId remotePeerId, DirectSessionId sessionId, SelfId selfIdentityId, CancellationToken cancellationToken);
}
