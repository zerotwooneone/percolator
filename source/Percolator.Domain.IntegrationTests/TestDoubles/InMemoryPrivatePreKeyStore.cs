using System.Collections.Concurrent;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.IntegrationTests.TestDoubles;

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly ConcurrentDictionary<string, StoredSignedPreKey> _signedPreKeys = new();
    private readonly ConcurrentDictionary<string, OneTimePreKeyMaterial> _oneTimePreKeys = new();

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}";
        _signedPreKeys.TryGetValue(key, out var stored);
        return Task.FromResult(stored?.PrivateKey);
    }

    public Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}:{keyId}";
        _oneTimePreKeys.TryRemove(key, out var material);
        return Task.FromResult(material?.PrivateKey);
    }

    public Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}";
        _signedPreKeys.TryGetValue(key, out var stored);
        return Task.FromResult(stored);
    }

    public Task StoreSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ownerId}:{deviceId}";
        _signedPreKeys[key] = new StoredSignedPreKey(privateKey, publicKey, signature);
        return Task.CompletedTask;
    }

    public Task StoreOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        IEnumerable<OneTimePreKeyMaterial> keys,
        CancellationToken cancellationToken = default)
    {
        foreach (var material in keys)
        {
            var dictKey = $"{ownerId}:{deviceId}:{material.KeyId}";
            _oneTimePreKeys[dictKey] = material;
        }
        return Task.CompletedTask;
    }

    public Task<uint> GetNextOneTimePreKeyIdAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var prefix = $"{ownerId}:{deviceId}:";
        var maxId = _oneTimePreKeys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => (uint?)uint.Parse(k[prefix.Length..]))
            .Max();

        return Task.FromResult((maxId ?? 0u) + 1u);
    }
}
