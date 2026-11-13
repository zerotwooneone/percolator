using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Identities.Ports;

/// <summary>A persisted signed pre-key with its public half and identity-key signature.</summary>
public sealed record StoredSignedPreKey(
    EphemeralPrivateKey PrivateKey,
    DhPublicKey PublicKey,
    DeviceLinkProof Signature);

/// <summary>A persisted one-time pre-key's public half (the private half is consumed/discarded).</summary>
public sealed record StoredOneTimePreKey(
    uint KeyId,
    DhPublicKey PublicKey);

/// <summary>A freshly generated one-time pre-key pair awaiting persistence.</summary>
public sealed record OneTimePreKeyMaterial(
    uint KeyId,
    EphemeralPrivateKey PrivateKey,
    DhPublicKey PublicKey);

/// <summary>
/// Port for retrieving and persisting the local device's private signed pre-keys and one-time pre-keys.
/// Used by the receiver during X3DH responder handshakes to compute master shared secrets.
/// </summary>
public interface IPrivatePreKeyStore
{
    Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default);

    Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    Task StoreSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default);

    Task StoreOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<OneTimePreKeyMaterial> keys,
        CancellationToken cancellationToken = default);

    Task<uint> GetNextOneTimePreKeyIdAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}
