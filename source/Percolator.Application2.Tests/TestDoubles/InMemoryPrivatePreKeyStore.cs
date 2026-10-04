using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly Dictionary<(PublicIdentityId, DeviceId), EphemeralPrivateKey> _signedPreKeys = [];
    private readonly Dictionary<(PublicIdentityId, DeviceId, uint), EphemeralPrivateKey> _oneTimePreKeys = [];

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        _signedPreKeys.TryGetValue((ownerId, deviceId), out var key);
        return Task.FromResult(key);
    }

    public Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        if (_oneTimePreKeys.Remove((ownerId, deviceId, keyId), out var key))
        {
            return Task.FromResult<EphemeralPrivateKey?>(key);
        }

        return Task.FromResult<EphemeralPrivateKey?>(null);
    }

    public Task StoreSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        CancellationToken cancellationToken = default)
    {
        _signedPreKeys[(ownerId, deviceId)] = privateKey;
        return Task.CompletedTask;
    }

    public Task StoreOneTimePreKeysPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<(uint KeyId, EphemeralPrivateKey Key)> keys,
        CancellationToken cancellationToken = default)
    {
        foreach (var (keyId, key) in keys)
        {
            _oneTimePreKeys[(ownerId, deviceId, keyId)] = key;
        }

        return Task.CompletedTask;
    }
}
