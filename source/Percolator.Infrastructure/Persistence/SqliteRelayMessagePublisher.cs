using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

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
            // Note: RecipientPublicIdentityId is the routing ID. Look up the actual PublicIdentityId from PeerIdentityDbo.
            var queueItems = new List<MessageQueueItemDbo>();
            foreach (var peerId in recipients)
            {
                // Look up the peer identity to get the PublicIdentityId
                var peerIdentity = await _db.PeerIdentities
                    .AsNoTracking()
                    .Where(p => p.PeerId == peerId.Value)
                    .FirstOrDefaultAsync(cancellationToken);
                
                if (peerIdentity == null)
                {
                    throw new InvalidOperationException($"No peer identity found for peer {peerId.Value}. Cannot determine PublicIdentityId for message routing.");
                }

                queueItems.Add(new MessageQueueItemDbo
                {
                    Id = Guid.NewGuid(),
                    AckId = Guid.NewGuid(),
                    RecipientPublicIdentityId = peerIdentity.PublicIdentityId,
                    Blob = payload.ToArray(),
                    EnqueuedAtUtc = DateTimeOffset.UtcNow
                });
            }

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
