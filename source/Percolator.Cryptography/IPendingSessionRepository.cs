namespace Percolator.Cryptography;

public interface IPendingSessionRepository
{
    Task AddAsync(PendingSession pending, CancellationToken cancellationToken = default);
    Task<PendingSession?> GetAsync(PendingSessionId id, CancellationToken cancellationToken = default);
    Task UpdateAsync(PendingSession pending, CancellationToken cancellationToken = default);
    Task DeleteAsync(PendingSessionId id, CancellationToken cancellationToken = default);
    IAsyncEnumerable<PendingSession> EnumerateAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<PendingSession> EnumerateExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
