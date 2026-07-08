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
        var peerIds = await _db.RelayBlindedRosters
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId.Value)
            .Select(e => e.MemberPublicIdentityId)
            .ToListAsync(cancellationToken);

        // The DBO stores PublicIdentityId, but we need to return ChatPeerId
        // This suggests a design mismatch - the DBO should likely store ChatPeerId instead
        // For now, we'll need to query the actual ChatPeerId from the participants table
        var conversation = await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == conversationId.Value, cancellationToken);

        if (conversation == null)
            return new List<ChatPeerId>();

        return conversation.Participants.Select(p => new ChatPeerId(p.ParticipantId)).ToList();
    }
}
