using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.ValueObjects;
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

    public async Task<GroupConversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var groupStateDbo = await _db.GroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == id.Value, cancellationToken);

        if (groupStateDbo is null)
            return null;

        // Load group members
        var groupMemberDbos = await _db.GroupMembers
            .Where(m => m.ConversationId == id.Value)
            .ToListAsync(cancellationToken);

        return ToDomain(groupStateDbo, groupMemberDbos);
    }

    public async Task AddAsync(GroupConversation conversation, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Persist GroupState
        var groupStateDbo = new Persistence.GroupStateDbo
        {
            ConversationId = conversation.Id.Value,
            Epoch = conversation.CurrentEpoch.Value,
            Name = conversation.Name.Value,
            AvatarId = conversation.AvatarId.ToArray(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.GroupStates.Add(groupStateDbo);

        // Persist GroupMembers
        foreach (var member in conversation.Members)
        {
            var groupMemberDbo = new Persistence.GroupMemberDbo
            {
                ConversationId = conversation.Id.Value,
                PublicIdentityId = member.ParticipantId.PublicIdentityId.Value,
                PeerId = member.ParticipantId is RemoteParticipantId remote ? remote.PeerId.Value : null,
                SelfId = member.ParticipantId is LocalParticipantId local ? local.SelfId.Value : null,
                Role = (Persistence.GroupMemberRole)member.Role,
                ProfileKey = [],
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(GroupConversation conversation, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Update GroupState
        var existingGroupState = await _db.GroupStates.FindAsync(new object[] { conversation.Id.Value }, cancellationToken);
        if (existingGroupState != null)
        {
            existingGroupState.Epoch = conversation.CurrentEpoch.Value;
            existingGroupState.Name = conversation.Name.Value;
            existingGroupState.AvatarId = conversation.AvatarId.ToArray();
            existingGroupState.UpdatedAtUtc = now;
            _db.GroupStates.Update(existingGroupState);
        }

        // Update GroupMembers - soft-delete support
        var existingMembers = await _db.GroupMembers
            .Where(m => m.ConversationId == conversation.Id.Value)
            .ToListAsync(cancellationToken);

        foreach (var member in conversation.Members)
        {
            var existingMember = existingMembers.FirstOrDefault(m => m.PublicIdentityId == member.ParticipantId.PublicIdentityId.Value);
            if (existingMember != null)
            {
                // Update existing member
                existingMember.Role = (Persistence.GroupMemberRole)member.Role;
                existingMember.RemovedAtUtc = member.RemovedAtUtc; // Soft-delete by setting RemovedAtUtc
            }
            else
            {
                // Add new member
                var groupMemberDbo = new Persistence.GroupMemberDbo
                {
                    ConversationId = conversation.Id.Value,
                    PublicIdentityId = member.ParticipantId.PublicIdentityId.Value,
                    PeerId = member.ParticipantId is RemoteParticipantId remote ? remote.PeerId.Value : null,
                    SelfId = member.ParticipantId is LocalParticipantId local ? local.SelfId.Value : null,
                    Role = (Persistence.GroupMemberRole)member.Role,
                    ProfileKey = [],
                    JoinedAtUtc = member.JoinedAtUtc,
                    RemovedAtUtc = member.RemovedAtUtc
                };
                _db.GroupMembers.Add(groupMemberDbo);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddWithOutboxAsync(GroupConversation conversation, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Persist GroupState
        var groupStateDbo = new Persistence.GroupStateDbo
        {
            ConversationId = conversation.Id.Value,
            Epoch = conversation.CurrentEpoch.Value,
            Name = conversation.Name.Value,
            AvatarId = conversation.AvatarId.ToArray(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.GroupStates.Add(groupStateDbo);

        // Persist GroupMembers
        foreach (var member in conversation.Members)
        {
            var groupMemberDbo = new Persistence.GroupMemberDbo
            {
                ConversationId = conversation.Id.Value,
                PublicIdentityId = member.ParticipantId.PublicIdentityId.Value,
                PeerId = member.ParticipantId is RemoteParticipantId remote ? remote.PeerId.Value : null,
                SelfId = member.ParticipantId is LocalParticipantId local ? local.SelfId.Value : null,
                Role = (Persistence.GroupMemberRole)member.Role,
                ProfileKey = [],
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static GroupConversation ToDomain(Persistence.GroupStateDbo groupStateDbo, List<Persistence.GroupMemberDbo> groupMemberDbos)
    {
        var groupMembers = groupMemberDbos.Select(m =>
        {
            ParticipantId participantId = m.PeerId.HasValue
                ? new RemoteParticipantId(new Percolator.Chat.GroupLedger.PublicIdentityId(m.PublicIdentityId), new ChatPeerId(m.PeerId.Value))
                : m.SelfId.HasValue
                    ? new LocalParticipantId(new Percolator.Chat.GroupLedger.PublicIdentityId(m.PublicIdentityId), new ChatSelfId(m.SelfId.Value))
                    : throw new InvalidOperationException($"GroupMember must have either PeerId or SelfId set for PublicIdentityId {m.PublicIdentityId}");

            return new GroupMember(
                new ConversationId(m.ConversationId),
                participantId,
                (GroupMemberRole)m.Role,
                m.JoinedAtUtc,
                m.RemovedAtUtc);
        }).ToList();

        var epoch = new GroupEpoch(groupStateDbo.Epoch);
        var name = new GroupName(groupStateDbo.Name ?? string.Empty);
        var avatarId = GroupAvatarId.FromBytesOwned(groupStateDbo.AvatarId ?? Array.Empty<byte>());

        return GroupConversation.Rehydrate(
            new ConversationId(groupStateDbo.ConversationId),
            name,
            epoch,
            avatarId,
            groupMembers);
    }
}
