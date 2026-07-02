using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Infrastructure.Chat.Persistence;
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
        note(
            "this query is not quite right. we need to ensure that there is exactly one relay per group. That needs to be reflected in the domain");
        var assignments = await _db.GroupMembers
            .Join(
                _db.GroupStates,
                member => member.ConversationId,
                state => state.ConversationId,
                (member, state) => new { member, state })
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
