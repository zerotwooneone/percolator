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

    public async Task<GroupConversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var groupStateDbo = await _db.GroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == id.Value, cancellationToken);

        if (groupStateDbo is null)
            return null;

        // Load group crypto state (1:1 relationship)
        var groupCryptoStateDbo = await _db.GroupCryptoStates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == id.Value, cancellationToken);

        if (groupCryptoStateDbo is null)
            return null;

        // Load group members
        var groupMemberDbos = await _db.GroupMembers
            .Where(m => m.ConversationId == id.Value)
            .ToListAsync(cancellationToken);

        return ToDomain(groupStateDbo, groupCryptoStateDbo, groupMemberDbos);
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
            PublicParams = conversation.PublicParams,
            RelayPeerId = conversation.RelayPeerId.Value,
            AvatarId = conversation.AvatarId.ToArray(),
            Description = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.GroupStates.Add(groupStateDbo);

        // Persist GroupCryptoState (1:1 relationship)
        var groupCryptoStateDbo = new GroupCryptoStateDbo
        {
            ConversationId = conversation.Id.Value,
            GroupMasterKeyBytes = conversation.MasterKey.ToArray(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.GroupCryptoStates.Add(groupCryptoStateDbo);

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
            existingGroupState.PublicParams = conversation.PublicParams;
            existingGroupState.AvatarId = conversation.AvatarId.Value;
            existingGroupState.UpdatedAtUtc = now;
            _db.GroupStates.Update(existingGroupState);
        }

        // Update GroupCryptoState (1:1 relationship)
        var existingCryptoState = await _db.GroupCryptoStates.FindAsync(new object[] { conversation.Id.Value }, cancellationToken);
        if (existingCryptoState != null)
        {
            existingCryptoState.GroupMasterKeyBytes = conversation.MasterKey.ToArray();
            existingCryptoState.UpdatedAtUtc = now;
            _db.GroupCryptoStates.Update(existingCryptoState);
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
            PublicParams = conversation.PublicParams,
            RelayPeerId = conversation.RelayPeerId.Value,
            AvatarId = conversation.AvatarId.ToArray(),
            Description = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.GroupStates.Add(groupStateDbo);

        // Persist GroupCryptoState (1:1 relationship)
        var groupCryptoStateDbo = new GroupCryptoStateDbo
        {
            ConversationId = conversation.Id.Value,
            GroupMasterKeyBytes = conversation.MasterKey.ToArray(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.GroupCryptoStates.Add(groupCryptoStateDbo);

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
                JoinedAtUtc = member.JoinedAtUtc,
                RemovedAtUtc = member.RemovedAtUtc
            };
            _db.GroupMembers.Add(groupMemberDbo);
        }

        // Note: Domain events are no longer used for group provisioning
        // The plan specified deleting GroupProvisioningRequestedDomainEvent and MemberInvitedDomainEvent

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static GroupConversation ToDomain(Persistence.GroupStateDbo groupStateDbo, GroupCryptoStateDbo groupCryptoStateDbo, List<Persistence.GroupMemberDbo> groupMemberDbos)
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

        var masterKey = Percolator.Chat.GroupLedger.GroupMasterKeyBytes.FromBytesOwned(groupCryptoStateDbo.GroupMasterKeyBytes);
        var publicParams = groupStateDbo.PublicParams;
        var epoch = new Percolator.Chat.GroupLedger.GroupEpoch(groupStateDbo.Epoch);
        var name = new Percolator.Chat.GroupLedger.GroupName(groupStateDbo.Name ?? string.Empty);
        var avatarId = new Percolator.Chat.GroupLedger.GroupAvatarId(groupStateDbo.AvatarId ?? Array.Empty<byte>());
        var relayPeerId = new ChatPeerId(groupStateDbo.RelayPeerId);

        return new GroupConversation(
            new ConversationId(groupStateDbo.ConversationId),
            name,
            masterKey,
            publicParams,
            epoch,
            avatarId,
            relayPeerId,
            groupMembers);
    }
}
