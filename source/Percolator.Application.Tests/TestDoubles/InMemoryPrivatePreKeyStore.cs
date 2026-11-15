using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly Dictionary<(PublicIdentityId, DeviceId, uint), StoredSignedPreKey> _signedPreKeys = [];
    private readonly Dictionary<(PublicIdentityId, DeviceId), uint> _currentSignedPreKeyIds = [];
    private readonly Dictionary<(PublicIdentityId, DeviceId, uint), OneTimePreKeyMaterial> _oneTimePreKeys = [];

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
        else if (!_currentSignedPreKeyIds.TryGetValue((ownerId, deviceId), out keyId))
        {
            return Task.FromResult<EphemeralPrivateKey?>(null);
        }

        _signedPreKeys.TryGetValue((ownerId, deviceId, keyId), out var stored);
        return Task.FromResult(stored?.PrivateKey);
    }

    public Task<EphemeralPrivateKey?> TryConsumeOneTimePreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        uint keyId,
        CancellationToken cancellationToken = default)
    {
        if (_oneTimePreKeys.Remove((ownerId, deviceId, keyId), out var material))
        {
            return Task.FromResult<EphemeralPrivateKey?>(material.PrivateKey);
        }

        return Task.FromResult<EphemeralPrivateKey?>(null);
    }

    public Task<StoredSignedPreKey?> GetSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        StoredSignedPreKey? stored = null;
        if (_currentSignedPreKeyIds.TryGetValue((ownerId, deviceId), out var keyId))
        {
            _signedPreKeys.TryGetValue((ownerId, deviceId, keyId), out stored);
        }
        return Task.FromResult(stored);
    }

    public Task<SignedPreKeyId> StoreSignedPreKeyAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        EphemeralPrivateKey privateKey,
        DhPublicKey publicKey,
        DeviceLinkProof signature,
        CancellationToken cancellationToken = default)
    {
        var nextKeyId = _signedPreKeys.Keys
            .Where(k => k.Item1 == ownerId && k.Item2 == deviceId)
            .Select(k => k.Item3)
            .DefaultIfEmpty(0u)
            .Max() + 1u;

        _signedPreKeys[(ownerId, deviceId, nextKeyId)] =
            new StoredSignedPreKey(new SignedPreKeyId(nextKeyId), privateKey, publicKey, signature, DateTimeOffset.UtcNow);
        _currentSignedPreKeyIds[(ownerId, deviceId)] = nextKeyId;
        return Task.FromResult(new SignedPreKeyId(nextKeyId));
    }

    public Task<int> PruneSignedPreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        DateTimeOffset olderThanUtc,
        int minRetainedCount = 3,
        CancellationToken cancellationToken = default)
    {
        _currentSignedPreKeyIds.TryGetValue((ownerId, deviceId), out var currentKeyId);

        var generations = _signedPreKeys
            .Where(kv => kv.Key.Item1 == ownerId && kv.Key.Item2 == deviceId)
            .Select(kv => (KeyId: kv.Key.Item3, Record: kv.Value))
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
            _signedPreKeys.Remove((ownerId, deviceId, g.KeyId));
        }
        return Task.FromResult(toDelete.Count);
    }

    public Task<int> CountAvailableOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var count = _oneTimePreKeys.Keys.Count(k => k.Item1 == ownerId && k.Item2 == deviceId);
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
            _oneTimePreKeys[(ownerId, deviceId, material.KeyId)] = material;
        }

        return Task.CompletedTask;
    }

    public Task<uint> GetNextOneTimePreKeyIdAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var maxId = _oneTimePreKeys.Keys
            .Where(k => k.Item1 == ownerId && k.Item2 == deviceId)
            .Select(k => (uint?)k.Item3)
            .Max();

        return Task.FromResult((maxId ?? 0u) + 1u);
    }
}
