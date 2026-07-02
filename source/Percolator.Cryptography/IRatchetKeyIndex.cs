namespace Percolator.Cryptography;

public interface IRatchetKeyIndex
{
    Task<SessionId?> TryResolveAsync(uint selfIdentityId, RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default);

    Task UpsertAsync(uint selfIdentityId, SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
