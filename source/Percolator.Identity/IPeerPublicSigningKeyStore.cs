namespace Percolator.Identity;

public interface IPeerPublicSigningKeyStore
{
    Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, byte[] publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<PeerId?> GetPeerIdByPublicKeyHashAsync(byte[] publicKeyHash, CancellationToken ct = default);
}
