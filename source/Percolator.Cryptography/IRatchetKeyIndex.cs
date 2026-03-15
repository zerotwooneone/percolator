namespace Percolator.Cryptography;

public interface IRatchetKeyIndex
{
    Task<SessionId?> TryResolveAsync(int selfIdentityId, RatchetEphemeralKey headerPublicKey, CancellationToken cancellationToken = default);

    Task UpsertAsync(int selfIdentityId, SessionId sessionId, RatchetEphemeralKey headerPublicKey, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
