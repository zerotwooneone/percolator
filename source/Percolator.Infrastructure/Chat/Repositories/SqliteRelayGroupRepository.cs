using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Repositories;

/// <summary>
/// SQLite implementation of the Relay group ledger repository.
/// </summary>
public sealed class SqliteRelayGroupRepository : IRelayGroupRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqliteRelayGroupRepository(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<RelayGroupLedger?> GetLedgerAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var dbo = await db.RelayGroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ConversationId == conversationId.Value, ct);

        if (dbo == null)
            return null;

        return new RelayGroupLedger(
            conversationId,
            dbo.Epoch,
            ZkGroupPublicParamsBytes.FromBytesOwned(dbo.GroupPublicParams));
    }

    public async Task SaveAsync(RelayGroupLedger ledger, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = await db.RelayGroupStates
            .FirstOrDefaultAsync(x => x.ConversationId == ledger.ConversationId.Value, ct);

        if (existing == null)
        {
            throw new InvalidOperationException("Cannot save a ledger that has not been provisioned. Use ProvisionAsync instead.");
        }

        existing.Epoch = ledger.CurrentEpoch;
        existing.GroupPublicParams = ledger.GroupPublicParams.ToArray();
        // Version is incremented automatically by EF Core's concurrency token

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var dbValues = await ex.Entries.Single().GetDatabaseValuesAsync(ct);
            if (dbValues == null)
                throw new LedgerDeletedDomainException();

            var winningEpoch = (uint)dbValues["Epoch"];
            throw new EpochConflictDomainException(winningEpoch);
        }
    }

    public async Task ProvisionAsync(RelayGroupLedger ledger, IReadOnlyList<byte[]> initialRoster, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        
        var newDbo = new RelayGroupStateDbo
        {
            ConversationId = ledger.ConversationId.Value,
            Epoch = ledger.CurrentEpoch,
            GroupPublicParams = ledger.GroupPublicParams.ToArray(),
            Version = 0
        };
        db.RelayGroupStates.Add(newDbo);

        foreach (var routingToken in initialRoster)
        {
            var rosterEntry = new RelayBlindedRosterDbo
            {
                ConversationId = ledger.ConversationId.Value,
                DestinationPkhBytes = routingToken
            };
            db.RelayBlindedRosters.Add(rosterEntry);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true)
        {
            throw new InvalidOperationException("Ledger already provisioned.", ex);
        }
    }
}
