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
}
