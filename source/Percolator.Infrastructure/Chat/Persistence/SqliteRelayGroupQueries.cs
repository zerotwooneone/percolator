using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayGroupQueries : IRelayGroupQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayGroupQueries(PercolatorDbContext db) => _db = db;

    public async Task<RelayGroupStateDto?> GetGroupStateAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == conversationId, cancellationToken);

        if (dbo == null)
            return null;

        return new RelayGroupStateDto(
            dbo.Epoch,
            RelayGroupPublicParamsBytes.FromBytesOwned(dbo.GroupPublicParams));
    }
}
