using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
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

    public async Task AddAsync(Message message, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = new MessageDbo
        {
            ConversationId = message.ConversationId.Value,
            PublicMessageId = message.Id.Value,
            SenderPeerId = message.SenderId is RemoteParticipantId remotePeerId ? remotePeerId.PeerId.Value : null,
            SenderSelfId = message.SenderId is LocalParticipantId localSelfId ? localSelfId.SelfId.Value : null,
            Body = message.Content,
            SentAt = message.Timestamp
        };

        _db.Messages.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Message message, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
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

    public async Task<Message?> GetByIdAsync(PublicMessageId id, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = await _db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.PublicMessageId == id.Value, cancellationToken);

        if (dbo is null)
            return null;

        ParticipantId senderId;
        if (dbo.SenderPeerId != null)
        {
            // Need to get PublicIdentityId for this ChatPeerId
            // For now, we'll create a RemoteParticipantId with a placeholder PublicIdentityId
            // This is a design issue - the DBO should store the full ParticipantId or PublicIdentityId
            var publicIdentityId = new PublicIdentityId(Guid.NewGuid()); // Placeholder - this needs to be fixed
            senderId = new RemoteParticipantId(publicIdentityId, new ChatPeerId(dbo.SenderPeerId.Value));
        }
        else if (dbo.SenderSelfId != null)
        {
            // Need to get PublicIdentityId for this ChatSelfId
            // For now, we'll create a LocalParticipantId with a placeholder PublicIdentityId
            // This is a design issue - the DBO should store the full ParticipantId or PublicIdentityId
            var publicIdentityId = new PublicIdentityId(Guid.NewGuid()); // Placeholder - this needs to be fixed
            senderId = new LocalParticipantId(publicIdentityId, new ChatSelfId(dbo.SenderSelfId.Value));
        }
        else
        {
            throw new InvalidOperationException("Message must have either SenderPeerId or SenderSelfId");
        }

        return new Message(
            new PublicMessageId(dbo.PublicMessageId),
            new ConversationId(dbo.ConversationId),
            senderId,
            dbo.Body,
            dbo.SentAt);
    }
}
