namespace Percolator.Cryptography;

public interface IPendingSessionRepository
{
    Task AddAsync(PendingSession pending, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    Task<PendingSession?> GetAsync(PendingSessionId id, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    Task UpdateAsync(PendingSession pending, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    Task DeleteAsync(PendingSessionId id, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PendingSession> EnumerateAsync(CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PendingSession> EnumerateExpiredAsync(CryptoSelfId selfIdentityId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
