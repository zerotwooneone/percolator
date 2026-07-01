using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;
using PublicMessageId = Percolator.Chat.Messaging.PublicMessageId;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteMessageRepository : IMessageRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteMessageRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Message message, int selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = new MessageDbo
        {
            ConversationId = message.ConversationId.Value,
            PublicMessageId = message.Id.Value,
            SenderId = message.SenderId.Value,
            Body = message.Content,
            SentAt = message.Timestamp
        };

        _db.Messages.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Message message, int selfIdentityId, CancellationToken cancellationToken)
    {
        var existing = await _db.Messages
            .FirstOrDefaultAsync(m => m.PublicMessageId == message.Id.Value, cancellationToken);

        if (existing is null)
        {
            // If not found, treat as add
            await AddAsync(message, selfIdentityId, cancellationToken);
            return;
        }

        existing.Body = message.Content;
        _db.Messages.Update(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Message?> GetByIdAsync(PublicMessageId id, int selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = await _db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.PublicMessageId == id.Value, cancellationToken);

        if (dbo is null)
            return null;

        return new Message(
            new PublicMessageId(dbo.PublicMessageId),
            new ConversationId(dbo.ConversationId),
            new ChatPeerId(dbo.SenderId),
            dbo.Body,
            dbo.SentAt);
    }
}
