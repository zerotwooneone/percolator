using Microsoft.EntityFrameworkCore;
using Percolator.Cryptography.GroupLedger;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography.GroupLedger;

public sealed class SqliteUnknownMessageCacheRepository : IUnknownMessageCacheRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteUnknownMessageCacheRepository(PercolatorDbContext db) => _db = db;

    public async Task<UnknownMessageCache?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        var dbo = await _db.UnknownMessageCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (dbo == null)
            return null;

        return new UnknownMessageCache(
            dbo.Id,
            GroupId.FromBytesOwned(dbo.ConversationId.ToByteArray()),
            dbo.MissingKeyId,
            Ciphertext.FromBytesOwned(dbo.Ciphertext),
            DateTimeOffset.FromUnixTimeMilliseconds(dbo.ReceivedAtUtc));
    }

    public async Task SaveAsync(UnknownMessageCache cache, CancellationToken cancellationToken)
    {
        var dbo = new UnknownMessageCacheDbo
        {
            Id = cache.Id,
            ConversationId = cache.GroupId.Value,
            MissingKeyId = cache.MissingKeyId,
            Ciphertext = cache.Ciphertext.ToArray(),
            ReceivedAtUtc = cache.ReceivedAtUtc.ToUnixTimeMilliseconds()
        };

        _db.UnknownMessageCaches.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var dbo = await _db.UnknownMessageCaches
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (dbo != null)
        {
            _db.UnknownMessageCaches.Remove(dbo);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
