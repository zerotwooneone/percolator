using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayRosterQueries : IRelayRosterQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayRosterQueries(PercolatorDbContext db) => _db = db;

    public async Task<IReadOnlyList<byte[]>> GetRoutingTokensAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        // Get the RoutingTokens directly from the blinded roster
        // The Relay uses these opaque tokens to route fan-out messages without knowing the true identities
        var routingTokens = await _db.RelayBlindedRosters
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId.Value)
            .Select(e => e.RoutingToken)
            .ToListAsync(cancellationToken);

        return routingTokens;
    }
}
