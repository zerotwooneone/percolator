using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Common.Services;

public sealed class DesktopLocalIdentityKeyStore : IDesktopIdentityKeyStore
{
    private readonly ConcurrentDictionary<PublicIdentityId, (byte[] PrivateKey, IdentityKey PublicKey)> _keys = new();

    public Task SaveKeyAsync(PublicIdentityId identityId, byte[] privateKey, IdentityKey publicKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(publicKey);

        _keys[identityId] = ((byte[])privateKey.Clone(), publicKey);
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        if (_keys.TryGetValue(identityId, out var entry))
        {
            return Task.FromResult<byte[]?>((byte[])entry.PrivateKey.Clone());
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
