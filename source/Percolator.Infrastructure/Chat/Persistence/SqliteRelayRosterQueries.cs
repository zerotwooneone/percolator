using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayRosterQueries : IRelayRosterQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayRosterQueries(PercolatorDbContext db) => _db = db;

    public async Task<IReadOnlyList<ChatPeerId>> GetMemberPeerIdsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var peerIds = await _db.RelayBlindedRosters
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .Select(e => e.BlindedChatPeerId)
            .ToListAsync(cancellationToken);

        return peerIds.Select(id => new ChatPeerId(id)).ToList();
    }
}
