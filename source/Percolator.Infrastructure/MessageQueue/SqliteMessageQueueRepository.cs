using Microsoft.EntityFrameworkCore;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using Percolator.MessageQueue.Abstractions;

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

            // Per-recipient count
            var recipientCount = await _db.MessageQueueItems
                .Where(x => x.RecipientPeerId.Value == recipientPeerId.Value)
                .CountAsync(cancellationToken);

            if (recipientCount >= PerRecipientMaxQueued)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, (uint)recipientCount, (uint)totalCount);
            }

            var item = new MessageQueueItemDbo
            {
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
        var cnt = await _db.MessageQueueItems
            .Where(x => x.RecipientPeerId.Value == peerId.Value)
            .CountAsync(ct);
        return (uint)cnt;
    }

    public async Task<IReadOnlyList<byte[]>> FetchAndDeleteAsync(PeerId recipientPeerId, int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<byte[]>();
        }

        // Cap batch size to a reasonable upper bound
        var take = Math.Min(maxCount, 500);

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var items = await _db.MessageQueueItems
                .Where(x => x.RecipientPeerId.Value == recipientPeerId.Value)
                .OrderBy(x => x.EnqueuedAtUtc)
                .Take(take)
                .ToListAsync(cancellationToken);

            if (items.Count == 0)
            {
                await tx.CommitAsync(cancellationToken);
                return Array.Empty<byte[]>();
            }

            var blobs = items.Select(i => i.Blob).ToList();

            _db.MessageQueueItems.RemoveRange(items);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return blobs;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
