using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
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

    public async Task<DirectConversation?> GetByIdAsync(ConversationId id, int selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id.Value && c.SelfIdentityId == selfIdentityId && c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct, cancellationToken);

        if (dbo is null)
            return null;

        return ToDomain(dbo);
    }

    public async Task AddAsync(DirectConversation conversation, int selfIdentityId, CancellationToken cancellationToken)
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

    public async Task<DirectConversation?> GetByParticipantPairAsync(int selfIdentityId, Guid otherPeerId, CancellationToken cancellationToken)
    {
        // Load self identity to obtain the local peer id
        var self = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(i => i.Id == selfIdentityId, cancellationToken)
            ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

        // Find a direct conversation for this self identity that contains both participants
        var dbo = await _db.Conversations
            .Include(c => c.Participants)
            .Where(c => c.SelfIdentityId == selfIdentityId)
            .Where(c => c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct)
            .Where(c => c.Participants.Any(p => p.ParticipantId == self.PeerId) && c.Participants.Any(p => p.ParticipantId == otherPeerId))
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task UpsertDirectSessionMappingAsync(int selfIdentityId, Guid directSessionId, ConversationId conversationId, CancellationToken cancellationToken)
    {
        var existingMap = await _db.DirectSessionConversations
            .FirstOrDefaultAsync(m => m.SelfIdentityId == selfIdentityId && m.DirectSessionId == directSessionId, cancellationToken);

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

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static DirectConversation ToDomain(ConversationDbo dbo)
    {
        var participants = dbo.Participants.Select(p => new ParticipantId(p.ParticipantId)).ToList();
        if (participants.Count != 2)
        {
            throw new InvalidOperationException($"Direct conversation must have exactly 2 participants, found {participants.Count}.");
        }

        return new DirectConversation(
            new ConversationId(dbo.Id),
            participants[0],
            participants[1]);
    }

    private static ConversationDbo ToDbo(DirectConversation conversation)
    {
        var dbo = new ConversationDbo
        {
            Id = conversation.Id.Value,
            Kind = Percolator.Infrastructure.Persistence.ConversationKind.Direct
        };

        dbo.Participants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversation.Id.Value,
            ParticipantId = conversation.Peer1.Value
        });

        dbo.Participants.Add(new ConversationParticipantDbo
        {
            ConversationId = conversation.Id.Value,
            ParticipantId = conversation.Peer2.Value
        });

        return dbo;
    }
}
