using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayRosterQueries : IRelayRosterQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayRosterQueries(PercolatorDbContext db) => _db = db;

    public async Task<IReadOnlyList<ChatPeerId>> GetMemberPeerIdsAsync(ConversationId conversationId, CancellationToken cancellationToken)
    {
        // Get the MemberPeerIds directly from the blinded roster
        var memberPeerIds = await _db.RelayBlindedRosters
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId.Value)
            .Select(e => e.MemberPeerId)
            .ToListAsync(cancellationToken);

        if (memberPeerIds.Count == 0)
            return new List<ChatPeerId>();

        // Lookup ChatPeerIds (ParticipantIds) by PeerIds for this conversation
        var participantIds = await _db.ConversationParticipants
            .AsNoTracking()
            .Where(cp => cp.ConversationId == conversationId.Value && memberPeerIds.Contains(cp.ParticipantId))
            .Select(cp => cp.ParticipantId)
            .ToListAsync(cancellationToken);

        return participantIds.Select(pid => new ChatPeerId(pid)).ToList();
    }
}
