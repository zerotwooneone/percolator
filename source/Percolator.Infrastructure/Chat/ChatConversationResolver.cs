using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Infrastructure.Chat;

/// <summary>
/// Infrastructure-backed resolver that maps routing keys to a local Conversation.
/// Currently supports DirectSessionId; PKH and Group GUID paths will be added next.
/// </summary>
public sealed class ChatConversationResolver : IConversationResolver
{
    private readonly PercolatorDbContext _db;

    public ChatConversationResolver(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<ConversationResolution> ResolveAsync(ConversationLookupKey lookupKey, CancellationToken cancellationToken)
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
            int selfIdentityId = session.SelfIdentityId;
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
                    .Include(c => c.Messages)
                    .FirstOrDefaultAsync(c => c.Id == mapping.ConversationId && c.SelfIdentityId == selfIdentityId, cancellationToken);
            }

            if (convo is null)
            {
                var remotePeerId = session.RemotePeerId; // value object PeerId

                // Deterministic channel id for 1:1 conversation based on ordered participant pair
                var channelId = ComputeDirectChannelId(new PeerId(selfIdentity.PeerId), remotePeerId);

                // Try to load existing conversation for this identity+channel
                convo = await _db.Conversations
                    .Include(c => c.Participants)
                    .Include(c => c.Messages)
                    .FirstOrDefaultAsync(c => c.SelfIdentityId == selfIdentityId && c.ChannelId == channelId, cancellationToken);

                if (convo is null)
                {
                    // Create new conversation with both participants
                    convo = new ConversationDbo
                    {
                        Id = Guid.NewGuid(),
                        ChannelId = channelId,
                        Name = null,
                        SelfIdentityId = selfIdentityId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };

                    var p1 = new ConversationParticipantDbo { ConversationId = convo.Id, ParticipantId = selfIdentity.PeerId };
                    var p2 = new ConversationParticipantDbo { ConversationId = convo.Id, ParticipantId = remotePeerId.Value };
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
            var participants = convo.Participants.Select(p => new ParticipantId(p.ParticipantId)).ToList();
            var messages = convo.Messages
                .OrderBy(m => m.SentAt)
                .Select(m => new Message(new MessageId(m.MessageGuid), new ParticipantId(m.SenderId), m.Body, m.SentAt))
                .ToList();
            var domain = new Conversation(new ConversationId(convo.Id), new ChannelId(convo.ChannelId), participants, messages, convo.Name);
            return new ConversationResolution(domain, selfIdentityId);
        }

        if (lookupKey.PublicKeyHash is not null)
        {
            throw new NotSupportedException("PKH-based conversation resolution not yet implemented.");
        }
        if (lookupKey.GroupConversationGuid.HasValue)
        {
            throw new NotSupportedException("Group GUID-based conversation resolution not yet implemented.");
        }

        throw new InvalidOperationException("Invalid routing key state.");
    }

    private static byte[] ComputeDirectChannelId(PeerId a, PeerId b)
    {
        // Order the pair to ensure symmetry
        var g1 = a.Value;
        var g2 = b.Value;
        var (left, right) = g1.CompareTo(g2) <= 0 ? (g1, g2) : (g2, g1);
        var input = $"direct:{left:D}:{right:D}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(input));
    }
}
