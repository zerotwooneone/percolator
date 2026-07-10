namespace Percolator.Identity;

public interface IPeerPublicSigningKeyStore
{
    Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, DateTimeOffset nowUtc, CancellationToken ct = default);
    /// <summary>
    /// Returns the PeerId for the given PublicIdentityId, or null if not found.
    /// Used for network ingress resolution.
    /// </summary>
    Task<PeerId?> GetPeerIdByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct = default);

}
