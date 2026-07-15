using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteGroupConversationQueries : IGroupConversationQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteGroupConversationQueries(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<GroupConversationDto>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var dtos = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Kind == ConversationKind.Group)
            .Join(
                _db.GroupStates,
                conversation => conversation.Id,
                groupState => groupState.ConversationId,
                (conversation, groupState) => new GroupConversationDto
                {
                    ConversationId = new ConversationId(conversation.Id),
                    RelayPeerId = new ChatPeerId(groupState.RelayPeerId),
                    Name = conversation.Name,
                    SelfIdentityId = new ChatSelfId(conversation.SelfIdentityId)
                })
            .ToListAsync(cancellationToken);

        return dtos;
    }
}
