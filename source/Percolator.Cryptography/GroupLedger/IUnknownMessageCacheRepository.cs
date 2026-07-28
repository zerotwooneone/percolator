namespace Percolator.Cryptography.GroupLedger;

public interface IUnknownMessageCacheRepository
{
    Task<UnknownMessageCache?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task SaveAsync(UnknownMessageCache cache, CancellationToken cancellationToken);
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
