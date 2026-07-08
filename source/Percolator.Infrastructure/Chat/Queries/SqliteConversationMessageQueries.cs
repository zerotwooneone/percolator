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
                SenderId = m.SenderPeerId != null ? Guid.Parse(m.SenderPeerId.Value.ToString()) : (m.SenderSelfId != null ? Guid.Parse(m.SenderSelfId.Value.ToString()) : Guid.Empty),
                Content = m.Body,
                Timestamp = m.SentAt
            })
            .ToListAsync(cancellationToken);

        // Order in memory since SQLite doesn't support DateTimeOffset in ORDER BY clauses
        messages = messages.OrderBy(m => m.Timestamp).ToList();

        return messages;
    }
}
