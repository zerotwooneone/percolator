namespace Percolator.Identity;

public interface IPeerPublicSigningKeyStore
{
    Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, byte[] publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<PeerId?> GetPeerIdByPublicKeyHashAsync(byte[] publicKeyHash, CancellationToken ct = default);
    /// <summary>
    /// Returns the latest active public key hash (PKH) for the given peer, or null if none is active.
    /// </summary>
    Task<byte[]?> GetPublicKeyHashByPeerIdAsync(PeerId peerId, CancellationToken ct = default);

    // Typed overloads for IdentityPublicKeyHash (non-breaking additions)
    Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, IdentityPublicKeyHash publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<PeerId?> GetPeerIdByPublicKeyHashAsync(IdentityPublicKeyHash publicKeyHash, CancellationToken ct = default);
    Task<IdentityPublicKeyHash?> GetPublicKeyHashByPeerIdTypedAsync(PeerId peerId, CancellationToken ct = default);
}
