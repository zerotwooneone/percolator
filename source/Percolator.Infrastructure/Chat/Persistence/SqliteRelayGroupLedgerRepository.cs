using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network.RelayLedger;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Network.RelayLedger;

public sealed class SqliteRelayGroupLedgerRepository : IRelayGroupLedgerRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayGroupLedgerRepository(PercolatorDbContext db) => _db = db;

    public async Task<RelayGroupLedger?> GetByIdAsync(RelayGroupId id, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupLedgers
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == id.Value, cancellationToken);

        if (dbo == null)
            return null;

        return RelayGroupLedger.Rehydrate(
            new RelayGroupId(dbo.ConversationId),
            new RelayGroupEpoch(dbo.Epoch),
            EncryptedEntriesBlobBytes.FromBytesOwned(dbo.EncryptedEntriesBlob),
            0);
    }

    public async Task CreateAsync(RelayGroupLedger ledger, CancellationToken cancellationToken)
    {
        var dbo = new RelayGroupLedgerDbo
        {
            ConversationId = ledger.Id.Value,
            Epoch = ledger.CurrentEpoch.Value,
            EncryptedEntriesBlob = ledger.EncryptedEntriesBlob.ToArray()
        };

        _db.RelayGroupLedgers.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task OverwriteStateAsync(RelayGroupLedger ledger, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupLedgers
            .FirstOrDefaultAsync(e => e.ConversationId == ledger.Id.Value, cancellationToken);

        if (dbo == null)
            throw new InvalidOperationException($"Relay group ledger not found for group {ledger.Id.Value}.");

        dbo.Epoch = ledger.CurrentEpoch.Value;
        dbo.EncryptedEntriesBlob = ledger.EncryptedEntriesBlob.ToArray();

        await _db.SaveChangesAsync(cancellationToken);
    }
}
