using System;
using System.Linq;
using System.Threading.Tasks;
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
        existing.ChannelId = conversation.ChannelId.Value; // should be stable but updating is harmless due to unique index
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

    private static Conversation ToDomain(ConversationDbo dbo)
    {
        var participants = dbo.Participants.Select(p => new ParticipantId(p.ParticipantId)).ToList();
        var messages = dbo.Messages
            .OrderBy(m => m.SentAt)
            .Select(m => new Message(new MessageId(m.MessageGuid), new ParticipantId(m.SenderId), m.Body, m.SentAt))
            .ToList();
        return new Conversation(new ConversationId(dbo.Id), new ChannelId(dbo.ChannelId), participants, messages, dbo.Name);
    }

    private static ConversationDbo ToDbo(Conversation conversation)
    {
        var dbo = new ConversationDbo
        {
            Id = conversation.Id.Value,
            ChannelId = conversation.ChannelId.Value,
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

