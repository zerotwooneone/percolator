using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat.ValueObjects;
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
            .OrderBy(m => m.SentAt)
            .Select(m => new MessageDto
            {
                MessageId = m.MessageGuid,
                ConversationId = m.ConversationId,
                SenderId = m.SenderId,
                Content = m.Body,
                Timestamp = m.SentAt
            })
            .ToListAsync(cancellationToken);

        return messages;
    }
}
