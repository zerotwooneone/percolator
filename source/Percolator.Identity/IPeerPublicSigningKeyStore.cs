namespace Percolator.Identity;

public interface IPeerPublicSigningKeyStore
{
    Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, IdentityPublicKeyHash publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<PeerId?> GetPeerIdByPublicKeyHashAsync(IdentityPublicKeyHash publicKeyHash, CancellationToken ct = default);
    /// <summary>
    /// Returns the latest active public key hash (PKH) for the given peer, or null if none is active.
    /// </summary>
    Task<IdentityPublicKeyHash?> GetPublicKeyHashByPeerIdAsync(PeerId peerId, CancellationToken ct = default);
}
