namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlGroupInvitationRepository : IGroupInvitationRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqlGroupInvitationRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        byte[]? initialMembersBytes = null;
        if (invitation.InitialMembers != null && invitation.InitialMembers.Count > 0)
        {
            initialMembersBytes = new byte[invitation.InitialMembers.Count * 16];
            for (int i = 0; i < invitation.InitialMembers.Count; i++)
            {
                invitation.InitialMembers[i].TryWriteBytes(initialMembersBytes.AsSpan(i * 16, 16));
            }
        }

        var entity = new ChatGroupInvitationEntity
        {
            Id = invitation.InvitationId.Value,
            TargetGroupChannelId = invitation.TargetGroupChannelId.Value,
            GroupName = invitation.GroupName,
            InviterId = invitation.InviterId.ToByteArray(),
            RelayEndpoint = invitation.RelayEndpoint,
            ExpiresAtUnixMs = invitation.ExpiresAtUnixMs,
            Status = (int)invitation.Status,
            RespondedAtUnixMs = invitation.RespondedAtUnixMs,
            InitialMembersBytes = initialMembersBytes
        };

        _dbContext.ChatGroupInvitations.Add(entity);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<PendingGroupInvitation?> GetByIdAsync(InvitationId invitationId, CancellationToken ct = default)
    {
        var idStr = invitationId.Value;
        var entity = await _dbContext.ChatGroupInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == idStr, ct);

        return entity == null ? null : ToDomainModel(entity);
    }

    public async Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        var idStr = invitation.InvitationId.Value;
        var entity = await _dbContext.ChatGroupInvitations
            .FirstOrDefaultAsync(i => i.Id == idStr, ct);

        if (entity == null)
        {
            throw new InvalidOperationException($"PendingGroupInvitation with ID '{idStr}' was not found.");
        }

        entity.Status = (int)invitation.Status;
        entity.RespondedAtUnixMs = invitation.RespondedAtUnixMs;

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PendingGroupInvitation>> GetAllPendingAsync(CancellationToken ct = default)
    {
        var entities = await _dbContext.ChatGroupInvitations
            .AsNoTracking()
            .Where(i => i.Status == (int)GroupInvitationStatus.Pending)
            .ToListAsync(ct);

        return entities.Select(ToDomainModel).ToList();
    }

    public async Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
    {
        var expired = await _dbContext.ChatGroupInvitations
            .Where(i => i.ExpiresAtUnixMs <= currentTimestampUnixMs || i.Status == (int)GroupInvitationStatus.Expired)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            return 0;
        }

        _dbContext.ChatGroupInvitations.RemoveRange(expired);
        await _dbContext.SaveChangesAsync(ct);
        return expired.Count;
    }

    private static PendingGroupInvitation ToDomainModel(ChatGroupInvitationEntity entity)
    {
        List<PublicIdentityId>? initialMembers = null;
        if (entity.InitialMembersBytes != null && entity.InitialMembersBytes.Length >= 16)
        {
            initialMembers = new List<PublicIdentityId>();
            for (int i = 0; i <= entity.InitialMembersBytes.Length - 16; i += 16)
            {
                initialMembers.Add(PublicIdentityId.FromBytes(entity.InitialMembersBytes.AsSpan(i, 16)));
            }
        }

        return new PendingGroupInvitation(
            new InvitationId(entity.Id),
            ChannelId.FromGuid(entity.TargetGroupChannelId),
            entity.GroupName,
            PublicIdentityId.FromBytes(entity.InviterId),
            entity.RelayEndpoint,
            entity.ExpiresAtUnixMs,
            (GroupInvitationStatus)entity.Status,
            entity.RespondedAtUnixMs,
            initialMembers: initialMembers);
    }
}
