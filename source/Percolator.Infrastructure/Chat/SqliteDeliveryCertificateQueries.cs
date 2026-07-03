using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteDeliveryCertificateQueries : IDeliveryCertificateQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteDeliveryCertificateQueries(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<(ChatSelfId SelfId, ChatPeerId RelayPeerId)>> GetActiveRelayAssignmentsAsync(CancellationToken ct)
    {
        var assignments = await _db.GroupStates
            .Join(
                _db.GroupMembers,
                state => state.ConversationId,
                member => member.ConversationId,
                (state, member) => new { state, member })
            .Where(x => x.member.SelfId != null && x.member.RemovedAtUtc == null)
            .Select(x => new
            {
                SelfId = x.member.SelfId!.Value,
                RelayPeerId = x.state.RelayPeerId.Value
            })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return assignments
            .Select(x => (new ChatSelfId(x.SelfId), new ChatPeerId(x.RelayPeerId)))
            .ToList();
    }
}
