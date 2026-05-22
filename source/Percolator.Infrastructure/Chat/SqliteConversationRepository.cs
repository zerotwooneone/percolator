using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteConversationRepository : IConversationRepository
{
    private readonly PercolatorDbContext _db;
    
    public SqliteConversationRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<Conversation?> GetByIdAsync(ConversationId id, int selfIdentityId)
    {
        var dbo = await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Participants)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id.Value && c.SelfIdentityId == selfIdentityId);
        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task AddAsync(Conversation conversation, int selfIdentityId)
    {
        var now = DateTimeOffset.UtcNow;
        var dbo = ToDbo(conversation);
        dbo.CreatedAt = now;
        dbo.UpdatedAt = now;
        dbo.SelfIdentityId = selfIdentityId;

        _db.Conversations.Add(dbo);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Conversation conversation, int selfIdentityId)
    {
        var existing = await _db.Conversations
            .Include(c => c.Participants)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == conversation.Id.Value && c.SelfIdentityId == selfIdentityId);
        if (existing is null)
        {
            // If not found, treat as add
            await AddAsync(conversation, selfIdentityId);
            return;
        }

        existing.Name = conversation.Name;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.SelfIdentityId = selfIdentityId;

        // Replace participants
        _db.ConversationParticipants.RemoveRange(existing.Participants);
        foreach (var p in conversation.Participants)
        {
            existing.Participants.Add(new ConversationParticipantDbo
            {
                ConversationId = existing.Id,
                ParticipantId = p.Value
            });
        }

        // Replace messages
        _db.Messages.RemoveRange(existing.Messages);
        foreach (var m in conversation.Messages)
        {
            existing.Messages.Add(new MessageDbo
            {
                ConversationId = existing.Id,
                MessageGuid = m.Id.Value,
                SenderId = m.SenderId.Value,
                Body = m.Content,
                SentAt = m.Timestamp
            });
        }

        _db.Conversations.Update(existing);
        await _db.SaveChangesAsync();
    }

    public async Task<Conversation?> GetByParticipantPairAsync(int selfIdentityId, Guid otherPeerId)
    {
        // Load self identity to obtain the local peer id
        var self = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(i => i.Id == selfIdentityId)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

        // Find a direct conversation for this self identity that contains both participants
        var dbo = await _db.Conversations
            .Include(c => c.Participants)
            .Include(c => c.Messages)
            .Where(c => c.SelfIdentityId == selfIdentityId)
            .Where(c => c.Participants.Any(p => p.ParticipantId == self.PeerId) && c.Participants.Any(p => p.ParticipantId == otherPeerId))
            .AsNoTracking()
            .FirstOrDefaultAsync();

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task UpsertDirectSessionMappingAsync(int selfIdentityId, Guid directSessionId, ConversationId conversationId)
    {
        var existingMap = await _db.DirectSessionConversations
            .FirstOrDefaultAsync(m => m.SelfIdentityId == selfIdentityId && m.DirectSessionId == directSessionId);

        if (existingMap is null)
        {
            _db.DirectSessionConversations.Add(new DirectSessionConversationDbo
            {
                SelfIdentityId = selfIdentityId,
                DirectSessionId = directSessionId,
                ConversationId = conversationId.Value
            });
        }
        else
        {
            existingMap.ConversationId = conversationId.Value;
            _db.DirectSessionConversations.Update(existingMap);
        }

        await _db.SaveChangesAsync();
    }

    private static Conversation ToDomain(ConversationDbo dbo)
    {
        var participants = dbo.Participants.Select(p => new ParticipantId(p.ParticipantId)).ToList();
        var messages = dbo.Messages
            .OrderBy(m => m.SentAt)
            .Select(m => new Message(new MessageId(m.MessageGuid), new ParticipantId(m.SenderId), m.Body, m.SentAt))
            .ToList();
        return new Conversation(new ConversationId(dbo.Id), participants, messages, dbo.Name);
    }

    private static ConversationDbo ToDbo(Conversation conversation)
    {
        var dbo = new ConversationDbo
        {
            Id = conversation.Id.Value,
            Name = conversation.Name
        };
        foreach (var p in conversation.Participants)
        {
            dbo.Participants.Add(new ConversationParticipantDbo
            {
                ConversationId = conversation.Id.Value,
                ParticipantId = p.Value
            });
        }
        foreach (var m in conversation.Messages)
        {
            dbo.Messages.Add(new MessageDbo
            {
                ConversationId = conversation.Id.Value,
                MessageGuid = m.Id.Value,
                SenderId = m.SenderId.Value,
                Body = m.Content,
                SentAt = m.Timestamp
            });
        }
        return dbo;
    }
}

