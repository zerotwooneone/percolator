using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteChatMessageWriter : IChatMessageWriter
{
    private readonly PercolatorDbContext _db;

    public SqliteChatMessageWriter(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task AddTextMessageAsync(
        ConversationId conversationId,
        ParticipantId participantId,
        string content,
        PublicMessageId publicMessageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        // Idempotency check
        var exists = await _db.Messages
            .AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId.Value && m.PublicMessageId == publicMessageId.Value, cancellationToken);
        if (exists)
        {
            return; // idempotent success
        }

        _db.Messages.Add(new MessageDbo
        {
            ConversationId = conversationId.Value,
            PublicMessageId = publicMessageId.Value,
            SenderPeerId = participantId is RemoteParticipantId remoteParticipantId ? remoteParticipantId.PeerId.Value : null,
            SenderSelfId = participantId is LocalParticipantId localParticipantId ? localParticipantId.SelfId.Value : null,
            Body = content,
            SentAt = sentAt
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Handle race: unique constraint hit means idempotent duplicate
            // Re-check and swallow if now present
            var nowExists = await _db.Messages
                .AsNoTracking()
                .AnyAsync(m => m.ConversationId == conversationId.Value && m.PublicMessageId == publicMessageId.Value, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }
    

    public async Task AddReadReceiptAsync(
        ConversationId conversationId,
        ChatSelfId selfIdentityId,
        ChatPeerId readerId,
        PublicMessageId publicMessageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        // Idempotency check: one receipt per reader per message
        var exists = await _db.ReadReceipts
            .AsNoTracking()
            .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == publicMessageId.Value && r.ReaderId == readerId.Value, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.ReadReceipts.Add(new ReadReceiptDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = publicMessageId.Value,
            ReaderId = readerId.Value,
            SentAt = sentAt
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var nowExists = await _db.ReadReceipts
                .AsNoTracking()
                .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == publicMessageId.Value && r.ReaderId == readerId.Value, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }

    
}
