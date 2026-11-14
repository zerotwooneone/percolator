using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class InMemoryPrivatePreKeyStore : IPrivatePreKeyStore
{
    private readonly Dictionary<(PublicIdentityId, DeviceId), StoredSignedPreKey> _signedPreKeys = [];
    private readonly Dictionary<(PublicIdentityId, DeviceId, uint), OneTimePreKeyMaterial> _oneTimePreKeys = [];

    public Task<EphemeralPrivateKey?> GetSignedPreKeyPrivateAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        _signedPreKeys.TryGetValue((ownerId, deviceId), out var stored);
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
        _signedPreKeys.TryGetValue((ownerId, deviceId), out var stored);
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
        _signedPreKeys[(ownerId, deviceId)] = new StoredSignedPreKey(privateKey, publicKey, signature);
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
