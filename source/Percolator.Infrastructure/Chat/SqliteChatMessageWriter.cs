using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupMembership;
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
        uint selfIdentityId,
        ChatPeerId senderId,
        string content,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        // Idempotency check
        var exists = await _db.Messages
            .AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId.Value && m.MessageGuid == messageId.Value, cancellationToken);
        if (exists)
        {
            return; // idempotent success
        }

        _db.Messages.Add(new MessageDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
            SenderId = senderId.Value,
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
                .AnyAsync(m => m.ConversationId == conversationId.Value && m.MessageGuid == messageId.Value, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }

    public async Task AddDeliveredReceiptAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId recipientId,
        MessageId messageId,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken)
    {
        // Idempotency check
        var exists = await _db.DeliveredReceipts
            .AsNoTracking()
            .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value && r.RecipientId == recipientId.Value, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.DeliveredReceipts.Add(new DeliveredReceiptDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
            RecipientId = recipientId.Value,
            DeliveredAt = deliveredAt
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var nowExists = await _db.DeliveredReceipts
                .AsNoTracking()
                .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value && r.RecipientId == recipientId.Value, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }

    public async Task AddReadReceiptAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId readerId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        // Idempotency check: one receipt per reader per message
        var exists = await _db.ReadReceipts
            .AsNoTracking()
            .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value && r.ReaderId == readerId.Value, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.ReadReceipts.Add(new ReadReceiptDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
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
                .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value && r.ReaderId == readerId.Value, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }

    public async Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        uint selfIdentityId,
        ChatPeerId reactorId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        var exists = await _db.EmojiReactions
            .AsNoTracking()
            .AnyAsync(e => e.ConversationId == conversationId.Value && e.MessageGuid == messageId.Value && e.ReactorId == reactorId.Value && e.Emoji == emoji, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.EmojiReactions.Add(new EmojiReactionDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
            ReactorId = reactorId.Value,
            Emoji = emoji,
            SentAt = sentAt
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var nowExists = await _db.EmojiReactions
                .AsNoTracking()
                .AnyAsync(e => e.ConversationId == conversationId.Value && e.MessageGuid == messageId.Value && e.ReactorId == reactorId.Value && e.Emoji == emoji, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }
}
