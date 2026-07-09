using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Queries;

public sealed class SqliteConversationMessageQueries : IConversationMessageQueries
{
    private readonly PercolatorDbContext _db;

    public SqliteConversationMessageQueries(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<List<MessageDto>> GetMessagesAsync(ConversationId conversationId, int selfIdentityId, CancellationToken cancellationToken = default)
    {
        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId.Value)
            .Select(m => new MessageDto
            {
                MessageId = m.PublicMessageId,
                ConversationId = m.ConversationId,
                SenderId = m.SenderPeerId != null 
                    ? _db.PeerIdentities.Where(p => p.PeerId == m.SenderPeerId).Select(p => p.PublicIdentityId).FirstOrDefault()
                    : (m.SenderSelfId != null 
                        ? _db.SelfIdentities.Where(s => s.Id == m.SenderSelfId).Select(s => s.PublicIdentityId).FirstOrDefault() 
                        : Guid.Empty),
                Content = m.Body,
                Timestamp = m.SentAt
            })
            .ToListAsync(cancellationToken);

        // Order in memory since SQLite doesn't support DateTimeOffset in ORDER BY clauses
        messages = messages.OrderBy(m => m.Timestamp).ToList();

        return messages;
    }
}
