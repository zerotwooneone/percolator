using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public sealed class SqliteRelayMessagePublisher : IRelayMessagePublisher
{
    private readonly PercolatorDbContext _db;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SqliteRelayMessagePublisher(PercolatorDbContext db) => _db = db;

    public async Task PublishAtomicAsync(
        RelayGroupLedger ledger,
        IReadOnlyList<ChatPeerId> recipients,
        QueuedPayloadBytes payload,
        CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(ledger.ConversationId.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            // 1. Manual Version Check
            var dbo = await _db.RelayGroupStates.FindAsync(new object[] { ledger.ConversationId.Value }, cancellationToken);
            if (dbo == null || ledger.Version != dbo.Version)
                throw new EpochConflictDomainException("Ledger version mismatch.");

            // 2. Update Ledger DBO
            dbo.Epoch = ledger.CurrentEpoch;
            dbo.GroupPublicParams = ledger.GroupPublicParams.ToArray();
            dbo.Version++;

            // 3. Fan-out: Map ChatPeerId (Domain) to MessageQueueItemDbo (Infrastructure)
            // Note: RecipientPeerId is the routing ID.
            var queueItems = recipients.Select(peerId => new MessageQueueItemDbo
            {
                Id = Guid.NewGuid(),
                AckId = Guid.NewGuid(),
                RecipientPeerId = new PeerId(peerId.Value), // Conversion: Domain to Infrastructure Identity type
                Blob = payload.ToArray(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow
            }).ToList();

            _db.MessageQueueItems.AddRange(queueItems);

            // 4. Atomic Commit
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
