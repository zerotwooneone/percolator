using System.Collections.Concurrent;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Domain.IntegrationTests.TestDoubles;

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly ConcurrentDictionary<string, EphemeralPrivateKey> _signedPreKeys = new();
    private readonly ConcurrentDictionary<string, EphemeralPrivateKey> _oneTimePreKeys = new();

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}";
        _signedPreKeys.TryGetValue(key, out var privKey);
        return Task.FromResult(privKey);
    }

    public Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}:{keyId}";
        _oneTimePreKeys.TryRemove(key, out var privKey);
        return Task.FromResult(privKey);
    }

    public Task StoreSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}";
        _signedPreKeys[key] = privateKey;
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
            var dictKey = $"{ownerId}:{deviceId}:{keyId}";
            _oneTimePreKeys[dictKey] = key;
        }
        return Task.CompletedTask;
    }
}
