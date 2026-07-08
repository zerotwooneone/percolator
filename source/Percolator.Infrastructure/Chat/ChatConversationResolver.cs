using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

/// <summary>
/// Infrastructure-backed resolver that maps routing keys to a local DirectConversation.
/// Currently supports DirectSessionId and PKH paths.
/// </summary>
public sealed class ChatConversationResolver : IDirectConversationResolver
{
    private readonly PercolatorDbContext _db;

    public ChatConversationResolver(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<DirectConversationResolution> ResolveAsync(ConversationLookupKey lookupKey, CancellationToken cancellationToken)
    {
        lookupKey.EnsureExactlyOne();

        if (lookupKey.DirectSessionId.HasValue)
        {
            var sessionGuid = lookupKey.DirectSessionId.Value;
            var session = await _db.DirectSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == sessionGuid, cancellationToken);
            if (session is null)
            {
                throw new InvalidOperationException($"No DirectSession found for SessionId={sessionGuid}.");
            }

            // Fetch identities
            var selfIdentityId = session.SelfIdentityId;
            var selfIdentity = await _db.SelfIdentities.AsNoTracking().FirstOrDefaultAsync(i => i.Id == selfIdentityId, cancellationToken)
                ?? throw new InvalidOperationException($"SelfIdentity not found for id {selfIdentityId}.");

            // First try mapping table: (SelfIdentityId, DirectSessionId) -> ConversationId
            var mapping = await _db.DirectSessionConversations
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.SelfIdentityId == selfIdentityId && m.DirectSessionId == sessionGuid, cancellationToken);

            ConversationDbo? convo = null;
            if (mapping is not null)
            {
                convo = await _db.Conversations
                    .Include(c => c.Participants)
                    .FirstOrDefaultAsync(c => c.Id == mapping.ConversationId && c.SelfIdentityId == selfIdentityId && c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct, cancellationToken);
            }

            if (convo is null)
            {
                var remotePeerId = session.RemotePeerId; // uint RemotePeerId
                // Try to load existing direct conversation for this identity by participant pair
                convo = await _db.Conversations
                    .Include(c => c.Participants)
                    .Where(c => c.SelfIdentityId == selfIdentityId)
                    .Where(c => c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct)
                    .Where(c => c.Participants.Any(p => p.ParticipantId == BitConverter.ToUInt32(selfIdentity.PublicIdentityId.Value.ToByteArray(), 0)) && c.Participants.Any(p => p.ParticipantId == remotePeerId))
                    .FirstOrDefaultAsync(cancellationToken);

                if (convo is null)
                {
                    // Create new conversation with both participants
                    convo = new ConversationDbo
                    {
                        Id = Guid.NewGuid(),
                        Name = null,
                        SelfIdentityId = selfIdentityId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Kind = Percolator.Infrastructure.Persistence.ConversationKind.Direct
                    };

                    var p1 = new ConversationParticipantDbo { ConversationId = convo.Id, ParticipantId = BitConverter.ToUInt32(selfIdentity.PublicIdentityId.Value.ToByteArray(), 0) };
                    var p2 = new ConversationParticipantDbo { ConversationId = convo.Id, ParticipantId = remotePeerId };
                    convo.Participants.Add(p1);
                    convo.Participants.Add(p2);

                    _db.Conversations.Add(convo);
                }

                // Ensure mapping exists now that we have a conversation id
                var newMap = new DirectSessionConversationDbo
                {
                    SelfIdentityId = selfIdentityId,
                    DirectSessionId = sessionGuid,
                    ConversationId = convo.Id
                };
                _db.DirectSessionConversations.Add(newMap);
                await _db.SaveChangesAsync(cancellationToken);
            }

            // Map to domain
            var participants = convo.Participants.Select(p => new ChatPeerId(p.ParticipantId)).ToList();
            if (participants.Count != 2)
            {
                throw new InvalidOperationException($"Direct conversation must have exactly 2 participants, found {participants.Count}.");
            }
            // Find which participant is the self (matches selfIdentityId)
            var selfParticipant = participants.FirstOrDefault(p => p.Value == selfIdentityId);
            var peerParticipant = participants.FirstOrDefault(p => p.Value != selfIdentityId);
            if (selfParticipant.Value == 0 || peerParticipant.Value == 0)
            {
                throw new InvalidOperationException("Could not determine self vs peer participants.");
            }
            var domain = new DirectConversation(new ConversationId(convo.Id), peerParticipant, new ChatSelfId(selfParticipant.Value));
            return new DirectConversationResolution(domain, new ChatSelfId(selfParticipant.Value));
        }

        if (lookupKey.PublicKeyHash is not null)
        {
            // Enforce a single local identity context for PKH path
            var identities = await _db.SelfIdentities.AsNoTracking().ToListAsync(cancellationToken);
            if (identities.Count != 1)
            {
                throw new InvalidOperationException("PKH resolution requires exactly one local self identity.");
            }
            var selfIdentity = identities[0];

            // Resolve remote peer from PKH
            var pkhBytes = lookupKey.PublicKeyHash.ToArray();
            var remoteKey = await _db.PeerPublicSigningKeys
                .AsNoTracking()
                .Where(k => k.PublicKeyHash == pkhBytes && k.ExpiredAtUtc == null)
                .OrderByDescending(k => k.ActiveAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (remoteKey is null)
            {
                throw new InvalidOperationException("No active peer signing key found for provided PKH.");
            }

            // Find or create the conversation for this self identity by participant pair
            var convo = await _db.Conversations
                .Include(c => c.Participants)
                .Where(c => c.SelfIdentityId == selfIdentity.Id)
                .Where(c => c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Direct)
                .Where(c => c.Participants.Any(p => p.ParticipantId == BitConverter.ToUInt32(selfIdentity.PublicIdentityId.Value.ToByteArray(), 0)) && c.Participants.Any(p => p.ParticipantId == remoteKey.PeerId.Value))
                .FirstOrDefaultAsync(cancellationToken);

            if (convo is null)
            {
                convo = new ConversationDbo
                {
                    Id = Guid.NewGuid(),
                    Name = null,
                    SelfIdentityId = selfIdentity.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Kind = Percolator.Infrastructure.Persistence.ConversationKind.Direct
                };

                var p1 = new ConversationParticipantDbo { ConversationId = convo.Id, ParticipantId = BitConverter.ToUInt32(selfIdentity.PublicIdentityId.Value.ToByteArray(), 0) };
                var p2 = new ConversationParticipantDbo { ConversationId = convo.Id, ParticipantId = remoteKey.PeerId.Value };
                convo.Participants.Add(p1);
                convo.Participants.Add(p2);

                _db.Conversations.Add(convo);
                await _db.SaveChangesAsync(cancellationToken);
            }

            var participants = convo.Participants.Select(p => new ChatPeerId(p.ParticipantId)).ToList();
            if (participants.Count != 2)
            {
                throw new InvalidOperationException($"Direct conversation must have exactly 2 participants, found {participants.Count}.");
            }
            // Find which participant is the self (matches selfIdentityId)
            var selfParticipant = participants.FirstOrDefault(p => p.Value == selfIdentity.Id);
            var peerParticipant = participants.FirstOrDefault(p => p.Value != selfIdentity.Id);
            if (selfParticipant.Value == 0 || peerParticipant.Value == 0)
            {
                throw new InvalidOperationException("Could not determine self vs peer participants.");
            }
            var domain = new DirectConversation(new ConversationId(convo.Id), peerParticipant, new ChatSelfId(selfParticipant.Value));
            return new DirectConversationResolution(domain, new ChatSelfId(selfParticipant.Value));
        }

        throw new InvalidOperationException("Invalid routing key state.");
    }
}
