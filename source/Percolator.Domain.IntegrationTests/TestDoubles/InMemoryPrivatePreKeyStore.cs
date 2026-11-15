using System.Collections.Concurrent;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.IntegrationTests.TestDoubles;

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly ConcurrentDictionary<string, StoredSignedPreKey> _signedPreKeys = new();
    private readonly ConcurrentDictionary<string, uint> _currentSignedPreKeyIds = new();
    private readonly ConcurrentDictionary<string, OneTimePreKeyMaterial> _oneTimePreKeys = new();

    private static string OwnerKey(PublicIdentityId ownerId, DeviceId deviceId) => $"{ownerId}:{deviceId}";
    private static string SpkKey(PublicIdentityId ownerId, DeviceId deviceId, uint keyId) => $"{ownerId}:{deviceId}:{keyId}";

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        SignedPreKeyId? signedPreKeyId = null,
        CancellationToken cancellationToken = default)
    {
        uint keyId;
        if (signedPreKeyId.HasValue)
        {
            keyId = signedPreKeyId.Value.Value;
        }
        else if (!_currentSignedPreKeyIds.TryGetValue(OwnerKey(ownerId, deviceId), out keyId))
        {
            return Task.FromResult<EphemeralPrivateKey?>(null);
        }

        _signedPreKeys.TryGetValue(SpkKey(ownerId, deviceId, keyId), out var stored);
        // Shared generation — return an owned clone; callers Dispose() after use
        // and must not zeroize the retained generation.
        return Task.FromResult(stored == null ? null : EphemeralPrivateKey.FromSpan(stored.PrivateKey.Span));
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
        StoredSignedPreKey? stored = null;
        if (_currentSignedPreKeyIds.TryGetValue(OwnerKey(ownerId, deviceId), out var keyId))
        {
            _signedPreKeys.TryGetValue(SpkKey(ownerId, deviceId, keyId), out stored);
        }
        return Task.FromResult(stored == null ? null : stored with
        {
            PrivateKey = EphemeralPrivateKey.FromSpan(stored.PrivateKey.Span)
        });
    }

    public Task<SignedPreKeyId> StoreSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default)
    {
        var ownerKey = OwnerKey(ownerId, deviceId);
        var prefix = $"{ownerKey}:";
        var nextKeyId = _signedPreKeys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => uint.Parse(k[prefix.Length..]))
            .DefaultIfEmpty(0u)
            .Max() + 1u;

        _signedPreKeys[SpkKey(ownerId, deviceId, nextKeyId)] =
            new StoredSignedPreKey(new SignedPreKeyId(nextKeyId), EphemeralPrivateKey.FromSpan(privateKey.Span), publicKey, signature, DateTimeOffset.UtcNow);
        _currentSignedPreKeyIds[ownerKey] = nextKeyId;
        return Task.FromResult(new SignedPreKeyId(nextKeyId));
    }

    public Task<int> PruneSignedPreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        DateTimeOffset olderThanUtc,
        int minRetainedCount = 3,
        CancellationToken cancellationToken = default)
    {
        var ownerKey = OwnerKey(ownerId, deviceId);
        var prefix = $"{ownerKey}:";
        _currentSignedPreKeyIds.TryGetValue(ownerKey, out var currentKeyId);

        var generations = _signedPreKeys
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (KeyId: uint.Parse(kv.Key[prefix.Length..]), Record: kv.Value))
            .OrderByDescending(g => g.KeyId)
            .ToList();

        var candidates = generations
            .Where(g => g.KeyId != currentKeyId && g.Record.CreatedAtUtc < olderThanUtc)
            .ToList();
        var retained = generations.Count - candidates.Count;
        var toDelete = candidates
            .OrderBy(g => g.KeyId)
            .Take(Math.Max(0, candidates.Count - Math.Max(0, minRetainedCount - retained)))
            .ToList();

        foreach (var g in toDelete)
        {
            _signedPreKeys.TryRemove(SpkKey(ownerId, deviceId, g.KeyId), out _);
        }
        return Task.FromResult(toDelete.Count);
    }

    public Task<int> CountAvailableOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var prefix = $"{ownerId}:{deviceId}:";
        var count = _oneTimePreKeys.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
        return Task.FromResult(count);
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
            // Clone the private half — publishers Dispose() generated materials
            // after storing, which would otherwise zero the stored key.
            _oneTimePreKeys[dictKey] = new OneTimePreKeyMaterial(
                material.KeyId,
                EphemeralPrivateKey.FromSpan(material.PrivateKey.Span),
                material.PublicKey);
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
