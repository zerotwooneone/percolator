using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.MessageQueue.Abstractions;
using System.Linq;

namespace Percolator.Infrastructure.MessageQueue;

public class SqliteMessageQueueRepository : IMessageQueueRepository
{
    // Enforced limits
    public const int GlobalMaxQueued = 10_000;
    public const int PerRecipientMaxQueued = 500;

    private readonly PercolatorDbContext _db;

    public SqliteMessageQueueRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<(bool Accepted, uint RecipientQueuedCount, uint TotalQueuedCount)> TryEnqueueAsync(
        PeerId recipientPeerId,
        byte[] messageBlob,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Global count
            var totalCount = await _db.MessageQueueItems.CountAsync(cancellationToken);
            if (totalCount >= GlobalMaxQueued)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, await CountRecipientAsync(recipientPeerId, cancellationToken), (uint)totalCount);
            }

            // Per-recipient count (client-side filter due to EF translation limitations on value objects)
            var recipientCount = _db.MessageQueueItems
                .AsEnumerable()
                .Count(x => x.RecipientPeerId.Value == recipientPeerId.Value);

            if (recipientCount >= PerRecipientMaxQueued)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, (uint)recipientCount, (uint)totalCount);
            }

            var item = new MessageQueueItemDbo
            {
                AckId = Guid.NewGuid(),
                RecipientPeerId = recipientPeerId,
                Blob = messageBlob,
                EnqueuedAtUtc = DateTimeOffset.UtcNow
            };

            _db.MessageQueueItems.Add(item);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return (true, (uint)(recipientCount + 1), (uint)(totalCount + 1));
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<uint> CountRecipientAsync(PeerId peerId, CancellationToken ct)
    {
        var cnt = _db.MessageQueueItems
            .AsEnumerable()
            .Count(x => x.RecipientPeerId.Value == peerId.Value);
        return (uint)cnt;
    }

    public async Task<IReadOnlyList<(Guid AckId, byte[] Blob)>> FetchAsync(PeerId recipientPeerId, int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<(Guid, byte[])>();
        }

        var take = Math.Min(maxCount, 500);

        var all = await _db.MessageQueueItems
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var items = all
            .Where(x => x.RecipientPeerId.Value == recipientPeerId.Value)
            .OrderBy(x => x.EnqueuedAtUtc)
            .Take(take)
            .Select(x => new { x.AckId, x.Blob })
            .ToList();

        return items
            .Select(x => (x.AckId, x.Blob))
            .ToList();
    }

    public async Task<bool> DeleteByAckIdAsync(Guid ackId, CancellationToken cancellationToken)
    {
        var row = await _db.MessageQueueItems.FirstOrDefaultAsync(x => x.AckId == ackId, cancellationToken);
        if (row is null)
        {
            return false;
        }
        _db.MessageQueueItems.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
