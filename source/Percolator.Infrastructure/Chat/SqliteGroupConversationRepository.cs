using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;
using Percolator.Infrastructure.Persistence;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

namespace Percolator.Infrastructure.Chat;

public sealed class SqliteGroupConversationRepository : IGroupConversationRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteGroupConversationRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task<GroupConversation?> GetByIdAsync(ConversationId id, uint selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = await _db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id.Value && c.SelfIdentityId == selfIdentityId && c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Group, cancellationToken);

        if (dbo is null)
            return null;

        // Load group state and members
        var groupStateDbo = await _db.GroupStates.FindAsync(new object[] { dbo.Id }, cancellationToken);
        var groupMemberDbos = await _db.GroupMembers
            .Where(m => m.ConversationId == dbo.Id)
            .ToListAsync(cancellationToken);

        return ToDomain(dbo, groupStateDbo, groupMemberDbos);
    }

    public async Task AddAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dbo = ToDbo(conversation);
        dbo.CreatedAt = now;
        dbo.UpdatedAt = now;
        dbo.SelfIdentityId = selfIdentityId;
        dbo.Kind = Percolator.Infrastructure.Persistence.ConversationKind.Group;
        dbo.Name = conversation.Name;

        _db.Conversations.Add(dbo);

        // Persist GroupState
        var groupStateDbo = new Persistence.GroupStateDbo
        {
            ConversationId = conversation.State.ConversationId.Value,
            Epoch = conversation.State.Epoch,
            Name = conversation.State.Name,
            PublicParams = conversation.State.PublicParams,
            CreatedAtUtc = conversation.State.CreatedAtUtc,
            UpdatedAtUtc = conversation.State.UpdatedAtUtc
        };
        _db.GroupStates.Add(groupStateDbo);

        // Persist GroupMembers
        foreach (var member in conversation.Members)
        {
            var groupMemberDbo = new Persistence.GroupMemberDbo
            {
                ConversationId = member.ConversationId.Value,
                MemberPkh = member.ParticipantId.Pkh,
                LocalPeerId = member.ParticipantId.LocalPeerId?.Value,
                Role = (Persistence.GroupMemberRole)member.Role,
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken cancellationToken)
    {
        var existing = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversation.Id.Value && c.SelfIdentityId == selfIdentityId && c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Group, cancellationToken);

        if (existing is null)
        {
            // If not found, treat as add
            await AddAsync(conversation, selfIdentityId, cancellationToken);
            return;
        }

        existing.Name = conversation.Name;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        // Update GroupState
        var existingGroupState = await _db.GroupStates.FindAsync(new object[] { existing.Id }, cancellationToken);
        if (existingGroupState != null)
        {
            existingGroupState.Epoch = conversation.State.Epoch;
            existingGroupState.Name = conversation.State.Name;
            existingGroupState.PublicParams = conversation.State.PublicParams;
            existingGroupState.UpdatedAtUtc = conversation.State.UpdatedAtUtc;
            _db.GroupStates.Update(existingGroupState);
        }

        // Update GroupMembers - replace all
        var existingMembers = await _db.GroupMembers
            .Where(m => m.ConversationId == existing.Id)
            .ToListAsync(cancellationToken);
        _db.GroupMembers.RemoveRange(existingMembers);

        foreach (var member in conversation.Members)
        {
            var groupMemberDbo = new Persistence.GroupMemberDbo
            {
                ConversationId = member.ConversationId.Value,
                MemberPkh = member.ParticipantId.Pkh,
                LocalPeerId = member.ParticipantId.LocalPeerId?.Value,
                Role = (Persistence.GroupMemberRole)member.Role,
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        _db.Conversations.Update(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddWithOutboxAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dbo = ToDbo(conversation);
        dbo.CreatedAt = now;
        dbo.UpdatedAt = now;
        dbo.SelfIdentityId = selfIdentityId;
        dbo.Kind = Percolator.Infrastructure.Persistence.ConversationKind.Group;
        dbo.Name = conversation.Name;

        _db.Conversations.Add(dbo);

        // Persist GroupState
        var groupStateDbo = new Persistence.GroupStateDbo
        {
            ConversationId = conversation.State.ConversationId.Value,
            Epoch = conversation.State.Epoch,
            Name = conversation.State.Name,
            PublicParams = conversation.State.PublicParams,
            CreatedAtUtc = conversation.State.CreatedAtUtc,
            UpdatedAtUtc = conversation.State.UpdatedAtUtc
        };
        _db.GroupStates.Add(groupStateDbo);

        // Persist GroupMembers
        foreach (var member in conversation.Members)
        {
            var groupMemberDbo = new Persistence.GroupMemberDbo
            {
                ConversationId = member.ConversationId.Value,
                MemberPkh = member.ParticipantId.Pkh,
                LocalPeerId = member.ParticipantId.LocalPeerId?.Value,
                Role = (Persistence.GroupMemberRole)member.Role,
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        // Map domain events to outbox
        var domainEvents = conversation.GetDomainEvents();
        foreach (var domainEvent in domainEvents)
        {
            var eventType = domainEvent.GetType().Name;
            var payloadJson = System.Text.Json.JsonSerializer.Serialize(domainEvent, domainEvent.GetType());
            
            // Determine destination PKH based on event type
            Pkh destinationPkh = domainEvent switch
            {
                Percolator.Chat.Events.GroupProvisioningRequestedDomainEvent provisioningEvent => conversation.RelayIdentity.Pkh,
                Percolator.Chat.Events.MemberInvitedDomainEvent inviteEvent => inviteEvent.ParticipantId.Pkh,
                _ => throw new InvalidOperationException($"Unknown domain event type: {eventType}")
            };

            var outboxItem = new RelayOutboxDbo
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                PayloadJson = payloadJson,
                DestinationPkh = destinationPkh,
                ProcessedAtUtc = null
            };
            _db.RelayOutbox.Add(outboxItem);
        }

        await _db.SaveChangesAsync(cancellationToken);
        conversation.ClearDomainEvents();
    }

    private static GroupConversation ToDomain(ConversationDbo dbo, Persistence.GroupStateDbo? groupStateDbo, List<Persistence.GroupMemberDbo> groupMemberDbos)
    {
        var groupState = groupStateDbo != null
            ? new GroupState(
                new ConversationId(groupStateDbo.ConversationId),
                groupStateDbo.Epoch,
                groupStateDbo.Name,
                groupStateDbo.PublicParams,
                groupStateDbo.CreatedAtUtc,
                groupStateDbo.UpdatedAtUtc)
            : throw new InvalidOperationException($"GroupState not found for conversation {dbo.Id}");

        var groupMembers = groupMemberDbos.Select(m => new GroupMember(
            new ConversationId(m.ConversationId),
            new GroupParticipantId(
                m.MemberPkh,
                m.LocalPeerId.HasValue ? new ChatPeerId(m.LocalPeerId.Value) : null),
            (GroupMemberRole)m.Role,
            m.JoinedAtUtc,
            m.RemovedAtUtc)).ToList();

        // TODO: Load relay identity from persistence - for now use placeholder
        var relayIdentity = new GroupParticipantId(
            Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytesOwned(Array.Empty<byte>()),
            null);

        return new GroupConversation(
            new ConversationId(dbo.Id),
            groupState,
            relayIdentity,
            groupMembers,
            dbo.Name);
    }

    private static ConversationDbo ToDbo(GroupConversation conversation)
    {
        return new ConversationDbo
        {
            Id = conversation.Id.Value,
            Name = conversation.Name,
            Kind = Percolator.Infrastructure.Persistence.ConversationKind.Group
        };
    }
}
