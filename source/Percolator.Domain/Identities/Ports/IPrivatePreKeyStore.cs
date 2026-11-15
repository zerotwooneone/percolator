using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Identities.Ports;

/// <summary>
/// A persisted signed pre-key with its assigned key id, public half, identity-key
/// signature, and creation timestamp. Multiple generations are retained so in-flight
/// handshakes keep decrypting across rotation; <see cref="IPrivatePreKeyStore.GetSignedPreKeyAsync"/>
/// returns the current generation.
/// </summary>
public sealed record StoredSignedPreKey(
    SignedPreKeyId KeyId,
    EphemeralPrivateKey PrivateKey,
    DhPublicKey PublicKey,
    DeviceLinkProof Signature,
    DateTimeOffset CreatedAtUtc);

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
    /// <summary>
    /// Returns the private half of a signed pre-key. When <paramref name="signedPreKeyId"/> is
    /// null the current generation is returned; otherwise the specific generation is returned
    /// (or null when it has been pruned).
    /// </summary>
    Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        SignedPreKeyId? signedPreKeyId = null,
        CancellationToken cancellationToken = default);

    Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the current signed pre-key (<c>IsCurrent</c>), or null when none exists.</summary>
    Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new signed pre-key generation: assigns the next monotonically increasing key id,
    /// marks it current, and demotes any previous generation. Older generations are retained for
    /// pruning. Returns the assigned key id.
    /// </summary>
    Task<SignedPreKeyId> StoreSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes signed pre-key generations older than <paramref name="olderThanUtc"/>, always
    /// retaining the <paramref name="minRetainedCount"/> most recent generations and never
    /// deleting the current one. Private key bytes are zeroized before deletion.
    /// Returns the number of generations deleted.
    /// </summary>
    Task<int> PruneSignedPreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        DateTimeOffset olderThanUtc,
        int minRetainedCount = 3,
        CancellationToken cancellationToken = default);

    /// <summary>Counts unconsumed one-time pre-keys remaining in the local pool.</summary>
    Task<int> CountAvailableOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
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
