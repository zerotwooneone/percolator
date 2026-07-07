namespace Percolator.Cryptography;

public interface IRatchetKeyIndex
{
    Task<SessionId?> TryResolveAsync(CryptoSelfId selfIdentityId, RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default);

    Task UpsertAsync(CryptoSelfId selfIdentityId, SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
