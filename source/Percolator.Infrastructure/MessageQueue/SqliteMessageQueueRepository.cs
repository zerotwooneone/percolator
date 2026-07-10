using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;

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
        PublicIdentityId recipientPublicIdentityId,
        QueuedPayloadBytes messageBlob,
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
                return (false, await CountRecipientAsync(recipientPublicIdentityId, cancellationToken), (uint)totalCount);
            }

            // Per-recipient count
            var recipientCount = await _db.MessageQueueItems
                .CountAsync(x => x.RecipientPublicIdentityId == recipientPublicIdentityId.Value, cancellationToken);

            if (recipientCount >= PerRecipientMaxQueued)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, (uint)recipientCount, (uint)totalCount);
            }

            var item = new MessageQueueItemDbo
            {
                AckId = Guid.NewGuid(),
                RecipientPublicIdentityId = recipientPublicIdentityId.Value,
                Blob = messageBlob.ToArray(),
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

    private async Task<uint> CountRecipientAsync(PublicIdentityId publicIdentityId, CancellationToken ct)
    {
        var cnt = await _db.MessageQueueItems
            .CountAsync(x => x.RecipientPublicIdentityId == publicIdentityId.Value, ct);
        return (uint)cnt;
    }

    public async Task<IReadOnlyList<(Guid AckId, QueuedPayloadBytes Blob)>> FetchAsync(PublicIdentityId recipientPublicIdentityId, int maxCount, CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            return Array.Empty<(Guid, QueuedPayloadBytes)>();
        }

        var take = Math.Min(maxCount, 500);

        var items = await _db.MessageQueueItems
            .AsNoTracking()
            .Where(x => x.RecipientPublicIdentityId == recipientPublicIdentityId.Value)
            .OrderBy(x => x.EnqueuedAtUtc)
            .Take(take)
            .Select(x => new { x.AckId, x.Blob })
            .ToListAsync(cancellationToken);

        return items
            .Select(x => (x.AckId, QueuedPayloadBytes.FromBytesOwned(x.Blob)))
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

    public async Task TryEnqueueBulkAsync(
        IReadOnlyList<PublicIdentityId> recipients,
        QueuedPayloadBytes messageBlob,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var items = recipients.Select(id => new MessageQueueItemDbo
            {
                AckId = Guid.NewGuid(),
                RecipientPublicIdentityId = id.Value,
                Blob = messageBlob.ToArray(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow
            }).ToList();

            _db.MessageQueueItems.AddRange(items);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
