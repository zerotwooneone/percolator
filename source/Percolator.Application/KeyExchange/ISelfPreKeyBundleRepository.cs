using Percolator.Identity;

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

        Task<(byte[] spkPrivate, byte[] spkPublicSpki, byte[] preKeySignature, DateTimeOffset expires)?> TryGetSignedPreKeyAsync(SelfId selfIdentityId, Guid signedPreKeyId, CancellationToken ct = default);
        Task<byte[]?> TryPopOneTimePreKeyPrivateAsync(SelfId selfIdentityId, Guid oneTimePreKeyId, CancellationToken ct = default);

        Task<(Guid otkId, byte[] otkPublicSpki)?> TryReserveOneTimePreKeyAsync(
            int selfIdentityId,
            Guid requestCorrelationId,
            DateTimeOffset reservedUntilUtc,
            CancellationToken ct = default);

        Task<byte[]?> TryConsumeReservedOneTimePreKeyPrivateAsync(
            int selfIdentityId,
            Guid requestCorrelationId,
            DateTimeOffset nowUtc,
            CancellationToken ct = default);

        Task<int> PurgeExpiredReservedOneTimePreKeysAsync(
            int selfIdentityId,
            DateTimeOffset nowUtc,
            CancellationToken ct = default);

        Task<bool> TryBurnReservedOneTimePreKeyAsync(
            int selfIdentityId,
            Guid requestCorrelationId,
            DateTimeOffset nowUtc,
            CancellationToken ct = default);
    }
}
