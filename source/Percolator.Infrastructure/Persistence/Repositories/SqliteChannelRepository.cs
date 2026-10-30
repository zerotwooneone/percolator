namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteChannelRepository : IChannelRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteChannelRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<DirectChannel?> GetDirectByIdAsync(ChannelId id, CancellationToken cancellationToken = default)
    {
        var channelIdGuid = id.Value;
        var entity = await _dbContext.ChatChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelIdGuid && c.Type == 0, cancellationToken);

        if (entity == null || !entity.OwnerIdentityId.HasValue || !entity.RemotePeerId.HasValue)
        {
            return null;
        }

        var payloads = await LoadPayloadsAsync(channelIdGuid, cancellationToken);

        return DirectChannel.Rehydrate(
            id,
            new PublicIdentityId(entity.OwnerIdentityId.Value),
            new PublicIdentityId(entity.RemotePeerId.Value),
            entity.CreatedAtUtc,
            entity.LastActivityUtc,
            payloads);
    }

    public async Task<DirectChannel?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;
        var remoteGuid = remotePeerId.Value;

        var entity = await _dbContext.ChatChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Type == 0 &&
                     c.OwnerIdentityId == ownerGuid &&
                     c.RemotePeerId == remoteGuid,
                cancellationToken);

        if (entity == null)
        {
            return null;
        }

        var payloads = await LoadPayloadsAsync(entity.Id, cancellationToken);

        return DirectChannel.Rehydrate(
            new ChannelId(entity.Id),
            ownerId,
            remotePeerId,
            entity.CreatedAtUtc,
            entity.LastActivityUtc,
            payloads);
    }

    public async Task<GroupChannel?> GetGroupByIdAsync(ChannelId id, CancellationToken cancellationToken = default)
    {
        var channelIdGuid = id.Value;
        var entity = await _dbContext.ChatChannels
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelIdGuid && c.Type == 1, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        var memberEntities = await _dbContext.ChatChannelMembers
            .AsNoTracking()
            .Where(m => m.ChannelId == channelIdGuid)
            .ToListAsync(cancellationToken);

        var members = memberEntities.Select(m => new ChannelMember(
            new PublicIdentityId(m.MemberIdentityId),
            (ChannelRole)m.Role,
            m.JoinedAtUtc)).ToList();

        var payloads = await LoadPayloadsAsync(channelIdGuid, cancellationToken);
        var epoch = entity.CurrentEpoch.HasValue ? new EpochNumber(entity.CurrentEpoch.Value) : EpochNumber.Genesis;

        return GroupChannel.Rehydrate(
            id,
            entity.Name,
            epoch,
            entity.CreatedAtUtc,
            entity.LastActivityUtc,
            members,
            payloads);
    }

    public async Task<IReadOnlyList<GroupChannel>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;

        // Find channel IDs where owner is a member
        var channelIds = await _dbContext.ChatChannelMembers
            .AsNoTracking()
            .Where(m => m.MemberIdentityId == ownerGuid)
            .Select(m => m.ChannelId)
            .ToListAsync(cancellationToken);

        var entities = await _dbContext.ChatChannels
            .AsNoTracking()
            .Where(c => c.Type == 1 && channelIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        var result = new List<GroupChannel>(entities.Count);
        foreach (var entity in entities)
        {
            var memberEntities = await _dbContext.ChatChannelMembers
                .AsNoTracking()
                .Where(m => m.ChannelId == entity.Id)
                .ToListAsync(cancellationToken);

            var members = memberEntities.Select(m => new ChannelMember(
                new PublicIdentityId(m.MemberIdentityId),
                (ChannelRole)m.Role,
                m.JoinedAtUtc)).ToList();

            var epoch = entity.CurrentEpoch.HasValue ? new EpochNumber(entity.CurrentEpoch.Value) : EpochNumber.Genesis;

            result.Add(GroupChannel.Rehydrate(
                new ChannelId(entity.Id),
                entity.Name,
                epoch,
                entity.CreatedAtUtc,
                entity.LastActivityUtc,
                members));
        }

        return result;
    }

    public async Task SaveDirectAsync(DirectChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var channelIdGuid = channel.Id.Value;
        var ownerGuid = channel.OwnerIdentityId.Value;
        var remoteGuid = channel.RemotePeerId.Value;

        var existing = await _dbContext.ChatChannels
            .FirstOrDefaultAsync(c => c.Id == channelIdGuid, cancellationToken);

        if (existing == null)
        {
            var entity = new ChatChannelEntity
            {
                Id = channelIdGuid,
                Type = 0, // Direct
                Name = string.Empty,
                OwnerIdentityId = ownerGuid,
                RemotePeerId = remoteGuid,
                CurrentEpoch = null,
                CreatedAtUtc = channel.CreatedAtUtc,
                LastActivityUtc = channel.LastActivityUtc
            };
            await _dbContext.ChatChannels.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.LastActivityUtc = channel.LastActivityUtc;
        }

        await SyncPayloadsAsync(channelIdGuid, channel.Payloads, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveGroupAsync(GroupChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var channelIdGuid = channel.Id.Value;

        var existing = await _dbContext.ChatChannels
            .FirstOrDefaultAsync(c => c.Id == channelIdGuid, cancellationToken);

        if (existing == null)
        {
            var entity = new ChatChannelEntity
            {
                Id = channelIdGuid,
                Type = 1, // Group
                Name = channel.Name,
                OwnerIdentityId = null,
                RemotePeerId = null,
                CurrentEpoch = channel.CurrentEpoch.Value,
                CreatedAtUtc = channel.CreatedAtUtc,
                LastActivityUtc = channel.LastActivityUtc
            };
            await _dbContext.ChatChannels.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.Name = channel.Name;
            existing.CurrentEpoch = channel.CurrentEpoch.Value;
            existing.LastActivityUtc = channel.LastActivityUtc;
        }

        // Sync members
        var existingMembers = await _dbContext.ChatChannelMembers
            .Where(m => m.ChannelId == channelIdGuid)
            .ToListAsync(cancellationToken);

        var currentMembers = channel.Members.ToDictionary(m => m.Id.Value);
        var existingMemberGuids = existingMembers.ToDictionary(m => m.MemberIdentityId);

        // Add/Update
        foreach (var (guid, member) in currentMembers)
        {
            if (!existingMemberGuids.TryGetValue(guid, out var existingMember))
            {
                await _dbContext.ChatChannelMembers.AddAsync(new ChatChannelMemberEntity
                {
                    ChannelId = channelIdGuid,
                    MemberIdentityId = guid,
                    Role = (int)member.Role,
                    JoinedAtUtc = member.JoinedAtUtc
                }, cancellationToken);
            }
            else
            {
                existingMember.Role = (int)member.Role;
            }
        }

        // Remove deleted
        foreach (var (guid, toRemove) in existingMemberGuids)
        {
            if (!currentMembers.ContainsKey(guid))
            {
                _dbContext.ChatChannelMembers.Remove(toRemove);
            }
        }

        await SyncPayloadsAsync(channelIdGuid, channel.Payloads, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<ChannelPayload>> LoadPayloadsAsync(Guid channelIdGuid, CancellationToken cancellationToken)
    {
        var entities = await _dbContext.ChatChannelPayloads
            .AsNoTracking()
            .Where(p => p.ChannelId == channelIdGuid)
            .OrderBy(p => p.SentAtUtc)
            .ToListAsync(cancellationToken);

        return entities.Select(p => new ChannelPayload(
            new PayloadId(p.Id),
            new ChannelId(p.ChannelId),
            new PublicIdentityId(p.AuthorId),
            new DeviceId(p.AuthorDeviceId),
            p.EncryptedPayload,
            p.SentAtUtc)).ToList();
    }

    private async Task SyncPayloadsAsync(Guid channelIdGuid, IReadOnlyList<ChannelPayload> payloads, CancellationToken cancellationToken)
    {
        if (payloads.Count == 0) return;

        var existingIds = await _dbContext.ChatChannelPayloads
            .Where(p => p.ChannelId == channelIdGuid)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var existingSet = new HashSet<Guid>(existingIds);

        foreach (var payload in payloads)
        {
            if (!existingSet.Contains(payload.Id.Value))
            {
                await _dbContext.ChatChannelPayloads.AddAsync(new ChatChannelPayloadEntity
                {
                    Id = payload.Id.Value,
                    ChannelId = channelIdGuid,
                    AuthorId = payload.AuthorId.Value,
                    AuthorDeviceId = payload.AuthorDeviceId.Value,
                    EncryptedPayload = payload.EncryptedPayload.ToArray(),
                    SentAtUtc = payload.SentAtUtc
                }, cancellationToken);
            }
        }
    }
}
