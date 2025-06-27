using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public interface IIdentityService
{
    /// <summary>
    /// Takes a public key, finds the corresponding peer, or creates a new one.
    /// </summary>
    /// <param name="publicKey">The verified public identity key of the peer.</param>
    /// <returns>The <see cref="PeerId"/> for the identity.</returns>
    Task<PeerId> GetOrCreatePeerAsync(OpaquePublicKey publicKey);
}
