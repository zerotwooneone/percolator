using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.Identities.Ports;

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

    Task StoreSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        CancellationToken cancellationToken = default);

    Task StoreOneTimePreKeysPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<(uint KeyId, EphemeralPrivateKey Key)> keys,
        CancellationToken cancellationToken = default);
}
