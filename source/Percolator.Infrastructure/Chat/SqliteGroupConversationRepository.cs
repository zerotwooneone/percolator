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

    public async Task<GroupConversation?> GetByIdAsync(ConversationId id, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
    {
        var dbo = await _db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.SelfIdentityId == selfIdentityId && c.Kind == Percolator.Infrastructure.Persistence.ConversationKind.Group, cancellationToken);

        if (dbo is null)
            return null;

        // Load group state and members
        var groupStateDbo = await _db.GroupStates.FindAsync(new object[] { dbo.Id }, cancellationToken);
        var groupMemberDbos = await _db.GroupMembers
            .Where(m => m.ConversationId == dbo.Id)
            .ToListAsync(cancellationToken);

        return ToDomain(dbo, groupStateDbo, groupMemberDbos);
    }

    public async Task AddAsync(GroupConversation conversation, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
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
            RelayPeerId = new PeerId(conversation.RelayPeerId.Value),
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
                PublicIdentityId = member.ParticipantId.PublicIdentityId,
                PeerId = member.ParticipantId is RemoteParticipantId remote ? new PeerId(remote.PeerId.Value) : null,
                SelfId = member.ParticipantId is LocalParticipantId local ? local.SelfId.Value : null,
                Role = (Persistence.GroupMemberRole)member.Role,
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(GroupConversation conversation, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
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
            existingGroupState.RelayPeerId = new PeerId(conversation.RelayPeerId.Value);
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
                PublicIdentityId = member.ParticipantId.PublicIdentityId,
                PeerId = member.ParticipantId is RemoteParticipantId remote ? new PeerId(remote.PeerId.Value) : null,
                SelfId = member.ParticipantId is LocalParticipantId local ? local.SelfId.Value : null,
                Role = (Persistence.GroupMemberRole)member.Role,
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        _db.Conversations.Update(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddWithOutboxAsync(GroupConversation conversation, ChatSelfId selfIdentityId, CancellationToken cancellationToken)
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
            RelayPeerId = new PeerId(conversation.RelayPeerId.Value),
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
                PublicIdentityId = member.ParticipantId.PublicIdentityId,
                PeerId = member.ParticipantId is RemoteParticipantId remote ? new PeerId(remote.PeerId.Value) : null,
                SelfId = member.ParticipantId is LocalParticipantId local ? local.SelfId.Value : null,
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

            // Determine destination PeerId based on event type
            // Pattern match on ParticipantId to extract PeerId for relay routing
            PeerId destinationPeerId = domainEvent switch
            {
                Percolator.Chat.Events.GroupProvisioningRequestedDomainEvent provisioningEvent => new PeerId(conversation.RelayPeerId.Value),
                Percolator.Chat.Events.MemberInvitedDomainEvent inviteEvent when inviteEvent.ParticipantId is RemoteParticipantId remote =>
                    new PeerId(remote.PeerId.Value),
                Percolator.Chat.Events.MemberInvitedDomainEvent inviteEvent when inviteEvent.ParticipantId is LocalParticipantId =>
                    throw new NotImplementedException("Cannot route to local participant via outbox"),
                _ => throw new InvalidOperationException($"Unknown domain event type: {eventType}")
            };

            var outboxItem = new RelayOutboxDbo
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                PayloadJson = payloadJson,
                DestinationPeerId = destinationPeerId,
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

        var groupMembers = groupMemberDbos.Select(m =>
        {
            ParticipantId participantId = m.PeerId.HasValue
                ? new RemoteParticipantId(m.PublicIdentityId, new ChatPeerId(m.PeerId.Value.Value))
                : m.SelfId.HasValue
                    ? new LocalParticipantId(m.PublicIdentityId, new ChatSelfId(m.SelfId.Value))
                    : throw new InvalidOperationException($"GroupMember must have either PeerId or SelfId set for PublicIdentityId {m.PublicIdentityId}");

            return new GroupMember(
                new ConversationId(m.ConversationId),
                participantId,
                (GroupMemberRole)m.Role,
                m.JoinedAtUtc,
                m.RemovedAtUtc);
        }).ToList();

        var relayPeerId = new ChatPeerId(groupStateDbo!.RelayPeerId.Value);

        return new GroupConversation(
            new ConversationId(dbo.Id),
            groupState,
            relayPeerId,
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
