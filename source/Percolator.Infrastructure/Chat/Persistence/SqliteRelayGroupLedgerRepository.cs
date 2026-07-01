using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayGroupLedgerRepository : IRelayGroupLedgerRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayGroupLedgerRepository(PercolatorDbContext db) => _db = db;

    public async Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == id.Value, cancellationToken);

        if (dbo == null)
            return null;

        return new RelayGroupLedger(
            new ConversationId(dbo.ConversationId),
            dbo.Epoch,
            RelayGroupPublicParamsBytes.FromBytesOwned(dbo.GroupPublicParams),
            dbo.Version);
    }

    public async Task ProvisionNewGroupAsync(ConversationId conversationId, RelayGroupPublicParamsBytes publicParams, IReadOnlyList<Percolator.Chat.GroupLedger.PublicIdentityId> memberPublicIdentityIds, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var existingGroup = await _db.RelayGroupStates
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.ConversationId == conversationId.Value, cancellationToken);

            if (existingGroup is not null)
            {
                return; // Already exists, idempotent
            }

            var relayGroupState = new RelayGroupStateDbo
            {
                ConversationId = conversationId.Value,
                GroupPublicParams = publicParams.ToArray(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            _db.RelayGroupStates.Add(relayGroupState);

            foreach (var publicIdentityId in memberPublicIdentityIds)
            {
                var blindedRosterEntry = new RelayBlindedRosterDbo
                {
                    ConversationId = conversationId.Value,
                    MemberPublicIdentityId = new Percolator.Identity.PublicIdentityId(publicIdentityId.Value),
                    AddedAtUtc = DateTimeOffset.UtcNow
                };
                _db.RelayBlindedRosters.Add(blindedRosterEntry);
            }

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
