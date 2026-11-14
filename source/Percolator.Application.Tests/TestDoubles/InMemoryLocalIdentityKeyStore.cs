using Percolator.Application.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class InMemoryLocalIdentityKeyStore : ILocalIdentityKeyStore
{
    private readonly Dictionary<PublicIdentityId, (EphemeralPrivateKey PrivateKey, IdentityKey PublicKey, ProfileKey ProfileKey)> _keys = [];

    public void RegisterKey(PublicIdentityId id, EphemeralPrivateKey privateKey, IdentityKey? publicKey = null, ProfileKey? profileKey = null)
    {
        var pub = publicKey ?? IdentityKey.FromSpan(new byte[32]);
        var profile = profileKey ?? ProfileKey.Generate();
        _keys[id] = (privateKey, pub, profile);
    }

    public Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (_keys.TryGetValue(identityId, out var entry))
        {
            return Task.FromResult<EphemeralPrivateKey?>(entry.PrivateKey);
        }
        return Task.FromResult<EphemeralPrivateKey?>(null);
    }

    public Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (_keys.TryGetValue(identityId, out var entry))
        {
            return Task.FromResult<IdentityKey?>(entry.PublicKey);
        }
        return Task.FromResult<IdentityKey?>(null);
    }

    public Task<ProfileKey?> GetProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        return Task.FromResult(_keys.TryGetValue(identityId, out var entry) ? entry.ProfileKey : null);
    }

    public Task SaveIdentityKeysAsync(
        PublicIdentityId identityId,
        EphemeralPrivateKey privateKey,
        IdentityKey publicKey,
        ProfileKey profileKey,
        CancellationToken ct = default)
    {
        _keys[identityId] = (privateKey, publicKey, profileKey);
        return Task.CompletedTask;
    }

    public Task<ProfileKey> RotateProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (!_keys.TryGetValue(identityId, out var entry))
        {
            throw new InvalidOperationException($"No identity keys stored for '{identityId.Value}'.");
        }

        var rotated = ProfileKey.Generate();
        _keys[identityId] = (entry.PrivateKey, entry.PublicKey, rotated);
        return Task.FromResult(rotated);
    }

    public Task DeleteIdentityKeysAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        _keys.Remove(identityId);
        return Task.CompletedTask;
    }
}
