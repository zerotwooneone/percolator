using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryLocalIdentityKeyStore : ILocalIdentityKeyStore
{
    private readonly Dictionary<PublicIdentityId, (byte[] PrivateKey, IdentityKey PublicKey)> _keys = [];

    public void RegisterKey(PublicIdentityId id, byte[] privateKey, IdentityKey? publicKey = null)
    {
        var pub = publicKey ?? IdentityKey.FromSpan(new byte[32]);
        _keys[id] = (privateKey, pub);
    }

    public Task<byte[]?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (_keys.TryGetValue(identityId, out var entry))
        {
            return Task.FromResult<byte[]?>(entry.PrivateKey);
        }
        return Task.FromResult<byte[]?>(null);
    }

    public Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (_keys.TryGetValue(identityId, out var entry))
        {
            return Task.FromResult<IdentityKey?>(entry.PublicKey);
        }
        return Task.FromResult<IdentityKey?>(null);
    }
}
