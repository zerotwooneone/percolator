using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.KeyExchange
{
    /// <summary>
    /// Local, per-identity store for responder pre-keys (SPK/OTK) that we publish to the host.
    /// Keys are partitioned by the current SelfIdentityId. OTKs are consumed at most once.
    /// </summary>
    public interface ISelfPreKeyBundleRepository
    {
        Task SaveSignedPreKeyAsync(int selfIdentityId, Guid signedPreKeyId, byte[] signedPreKeyPrivate, byte[] signedPreKeyPublicSpki, byte[] preKeySignature, DateTimeOffset expires, CancellationToken ct = default);
        Task SaveOneTimePreKeysAsync(int selfIdentityId, IEnumerable<(Guid otkId, byte[] otkPrivate, byte[] otkPublicSpki)> oneTimePreKeys, CancellationToken ct = default);

        Task<(byte[] spkPrivate, byte[] spkPublicSpki, byte[] preKeySignature, DateTimeOffset expires)?> TryGetSignedPreKeyAsync(int selfIdentityId, Guid signedPreKeyId, CancellationToken ct = default);
        Task<byte[]?> TryPopOneTimePreKeyPrivateAsync(int selfIdentityId, Guid oneTimePreKeyId, CancellationToken ct = default);
    }
}
