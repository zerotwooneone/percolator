using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Queries;

public sealed class SqliteRelayBlindedRosterQueries : IRelayBlindedRosterQueries
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqliteRelayBlindedRosterQueries(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<byte[]>> GetBlindedRosterAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.RelayBlindedRosters
            .AsNoTracking()
            .Where(x => x.ConversationId == conversationId.Value)
            .Select(x => x.DestinationPkhBytes)
            .ToListAsync(ct);
    }
}
