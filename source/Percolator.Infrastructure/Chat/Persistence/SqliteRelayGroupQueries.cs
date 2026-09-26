using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayGroupQueries : IRelayGroupQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayGroupQueries(PercolatorDbContext db) => _db = db;

    public async Task<RelayGroupStateDto?> GetGroupStateAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupLedgers
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == conversationId.Value, cancellationToken);

        if (dbo == null)
            return null;

        return new RelayGroupStateDto(
            new RelayGroupEpoch(dbo.Epoch),
            EncryptedEntriesBlobBytes.FromBytesOwned(dbo.EncryptedEntriesBlob));
    }
}
