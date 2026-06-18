using Percolator.Identity;

namespace Percolator.Application.Chat;

/// <summary>
/// Service for resolving blinded routing tokens (PKH bytes) to local PeerId identities.
/// </summary>
public interface IRelayTargetResolver
{
    /// <summary>
    /// Resolves a list of destination PKH bytes to PeerId entities.
    /// Creates new PeerIdentity aggregates if they don't exist.
    /// </summary>
    Task<List<PeerId>> ResolveTargetsAsync(List<IdentityPublicKeyHash> destinationPkhBytes, CancellationToken ct = default);
}
