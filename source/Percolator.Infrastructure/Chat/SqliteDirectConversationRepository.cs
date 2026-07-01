using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteDirectConversationRepository : IDirectConversationRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteDirectConversationRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<DirectConversation?> GetByIdAsync(ConversationId id, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id && c.SelfIdentityId == selfIdentityId && c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct, cancellationToken);

        if (dbo is null)
            return null;

        return ToDomain(dbo);
    }

    public async Task AddAsync(DirectConversation conversation, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dbo = ToDbo(conversation);
        dbo.CreatedAt = now;
        dbo.UpdatedAt = now;
        dbo.SelfIdentityId = selfIdentityId;
        dbo.Kind = Percolator.Infrastructure.Persistence.ConversationKind.Direct;

        _db.Conversations.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DirectConversation?> GetByParticipantPairAsync(ChatSelfId selfIdentityId, ChatPeerId otherPeerId, CancellationToken cancellationToken)
    {
        // Load self identity to obtain the local peer id
        var self = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(i => i.Id.Value == selfIdentityId.Value, cancellationToken)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

        // Find a direct conversation for this self identity that contains both participants
        var dbo = await _db.Conversations
            .Include(c => c.Participants)
            .Where(c => c.SelfIdentityId == selfIdentityId)
            .Where(c => c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct)
            .Where(c => c.Participants.Any(p => p.ParticipantId == otherPeerId))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task UpsertDirectSessionMappingAsync(ChatSelfId selfIdentityId, Guid directSessionId, ConversationId conversationId, CancellationToken cancellationToken)
    {
        var existingMap = await _db.DirectSessionConversations
            .FirstOrDefaultAsync(m => m.SelfIdentityId == selfIdentityId && m.DirectSessionId == directSessionId, cancellationToken);

        if (existingMap is null)
        {
            _db.DirectSessionConversations.Add(new DirectSessionConversationDbo
            {
                SelfIdentityId = selfIdentityId,
                DirectSessionId = directSessionId,
                ConversationId = conversationId
            });
        }
        else
        {
            existingMap.ConversationId = conversationId;
            _db.DirectSessionConversations.Update(existingMap);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static DirectConversation ToDomain(ConversationDbo dbo)
    {
        var participants = dbo.Participants.Select(p => p.ParticipantId).ToList();
        if (participants.Count != 1)
        {
            throw new InvalidOperationException($"Direct conversation must have exactly 1 participants, found {participants.Count}.");
        }

        return new DirectConversation(
            dbo.Id,
            participants[0],
            dbo.SelfIdentityId);
    }

    private static ConversationDbo ToDbo(DirectConversation conversation)
    {
        var dbo = new ConversationDbo
        {
            Id = conversation.Id,
            Kind = Percolator.Infrastructure.Persistence.ConversationKind.Direct
        };

        dbo.Participants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversation.Id,
            ParticipantId = conversation.Peer1
        });

        return dbo;
    }
}
