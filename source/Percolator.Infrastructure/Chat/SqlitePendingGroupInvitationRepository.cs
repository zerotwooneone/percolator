using Microsoft.EntityFrameworkCore;
using Percolator.Chat;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;
using System.Text.Json;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Chat;

public sealed class SqlitePendingGroupInvitationRepository : IPendingGroupInvitationRepository
{
    private readonly PercolatorDbContext _db;

    public SqlitePendingGroupInvitationRepository(PercolatorDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(PendingGroupInvitation invitation, CancellationToken cancellationToken = default)
    {
        var initialMembersJson = JsonSerializer.Serialize(invitation.InitialMembers);
        
        var dbo = new PendingGroupInvitationDbo
        {
            Id = invitation.Id,
            ConversationId = invitation.ConversationId.Value,
            InviterPeerId = invitation.InviterPeerId.Value,
            CreatorIdentityKey = invitation.CreatorIdentityKey,
            InitialMembersJson = initialMembersJson,
            GroupName = invitation.GroupName,
            ReceivedAtUtc = invitation.ReceivedAtUtc,
            Status = (Persistence.PendingGroupInvitationStatus)invitation.Status
        };
        _db.PendingGroupInvitations.Add(dbo);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.PendingGroupInvitations.FirstOrDefaultAsync(i => i.Id == invitation.Id, cancellationToken);
        if (dbo != null)
        {
            dbo.Status = (Persistence.PendingGroupInvitationStatus)invitation.Status;
            _db.PendingGroupInvitations.Update(dbo);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<PendingGroupInvitation?> GetByConversationIdAsync(ConversationId conversationId, CancellationToken cancellationToken = default)
    {
        var dbo = await _db.PendingGroupInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.ConversationId == conversationId.Value && i.Status == Persistence.PendingGroupInvitationStatus.Pending, cancellationToken);

        if (dbo == null) return null;

        List<byte[]> initialMembers;
        try
        {
            initialMembers = JsonSerializer.Deserialize<List<byte[]>>(dbo.InitialMembersJson) ?? new List<byte[]>();
        }
        catch (JsonException)
        {
            initialMembers = new List<byte[]>();
        }

        return new PendingGroupInvitation(
            dbo.Id,
            new ConversationId(dbo.ConversationId),
            new ChatPeerId(dbo.InviterPeerId),
            dbo.CreatorIdentityKey,
            initialMembers,
            dbo.GroupName,
            dbo.ReceivedAtUtc,
            (Percolator.Chat.GroupMembership.PendingGroupInvitationStatus)dbo.Status
        );
    }
}
