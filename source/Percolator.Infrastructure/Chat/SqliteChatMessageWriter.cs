using Microsoft.EntityFrameworkCore;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
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
        int selfIdentityId,
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

        // Determine sender for direct conversations: pick the participant that is not self
        var convo = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == conversationId.Value && c.SelfIdentityId == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found for selfIdentityId={selfIdentityId}.");

        var selfIdentity = await _db.SelfIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

        var senderId = convo.Participants
            .Select(p => p.ParticipantId)
            .FirstOrDefault(p => p != selfIdentity.PeerId);
        if (senderId == Guid.Empty)
        {
            // Fallback to self as sender if we didn't find another participant (e.g., group path TBA)
            senderId = selfIdentity.PeerId;
        }

        _db.Messages.Add(new MessageDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
            SenderId = senderId,
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

    public async Task AddReadReceiptAsync(
        ConversationId conversationId,
        int selfIdentityId,
        MessageId messageId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        // Idempotency check: one receipt per reader per message
        var convo = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == conversationId.Value && c.SelfIdentityId == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found for selfIdentityId={selfIdentityId}.");

        var selfIdentity = await _db.SelfIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

        var readerId = convo.Participants
            .Select(p => p.ParticipantId)
            .FirstOrDefault(p => p != selfIdentity.PeerId);
        if (readerId == Guid.Empty)
        {
            readerId = selfIdentity.PeerId;
        }

        var exists = await _db.ReadReceipts
            .AsNoTracking()
            .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value && r.ReaderId == readerId, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.ReadReceipts.Add(new ReadReceiptDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
            ReaderId = readerId,
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
                .AnyAsync(r => r.ConversationId == conversationId.Value && r.MessageGuid == messageId.Value && r.ReaderId == readerId, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }

    public async Task AddEmojiAnnotationAsync(
        ConversationId conversationId,
        int selfIdentityId,
        MessageId messageId,
        string emoji,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken)
    {
        var convo = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == conversationId.Value && c.SelfIdentityId == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found for selfIdentityId={selfIdentityId}.");

        var selfIdentity = await _db.SelfIdentities
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

        var reactorId = convo.Participants
            .Select(p => p.ParticipantId)
            .FirstOrDefault(p => p != selfIdentity.PeerId);
        if (reactorId == Guid.Empty)
        {
            reactorId = selfIdentity.PeerId;
        }

        var exists = await _db.EmojiReactions
            .AsNoTracking()
            .AnyAsync(e => e.ConversationId == conversationId.Value && e.MessageGuid == messageId.Value && e.ReactorId == reactorId && e.Emoji == emoji, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.EmojiReactions.Add(new EmojiReactionDbo
        {
            ConversationId = conversationId.Value,
            MessageGuid = messageId.Value,
            ReactorId = reactorId,
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
                .AnyAsync(e => e.ConversationId == conversationId.Value && e.MessageGuid == messageId.Value && e.ReactorId == reactorId && e.Emoji == emoji, cancellationToken);
            if (!nowExists)
            {
                throw;
            }
        }
    }
}
