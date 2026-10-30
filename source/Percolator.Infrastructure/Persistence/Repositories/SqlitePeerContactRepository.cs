namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlitePeerContactRepository : IPeerContactRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePeerContactRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<PeerContact?> GetByPeerIdAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;
        var remoteGuid = remotePeerId.Value;

        var entity = await _dbContext.PeerContacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerIdentityId == ownerGuid && c.RemotePeerId == remoteGuid, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        var devices = await _dbContext.PeerContactDevices
            .AsNoTracking()
            .Where(d => d.OwnerIdentityId == ownerGuid && d.RemotePeerId == remoteGuid)
            .Select(d => new DeviceId(d.DeviceId))
            .ToListAsync(cancellationToken);

        var pubKey = entity.PrimaryPublicKey != null && entity.PrimaryPublicKey.Length == 32
            ? IdentityKey.FromSpan(entity.PrimaryPublicKey)
            : null;

        var nickname = ContactNickname.Create(entity.Nickname, remotePeerId);

        return PeerContact.Rehydrate(
            ownerId,
            remotePeerId,
            nickname,
            (PeerTrustLevel)entity.TrustLevel,
            (ContactState)entity.State,
            entity.CreatedAtUtc,
            entity.LastSeenAtUtc,
            pubKey,
            devices);
    }

    public async Task<IReadOnlyList<PeerContact>> GetAllForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        var ownerGuid = ownerId.Value;

        var entities = await _dbContext.PeerContacts
            .AsNoTracking()
            .Where(c => c.OwnerIdentityId == ownerGuid)
            .ToListAsync(cancellationToken);

        var allDevices = await _dbContext.PeerContactDevices
            .AsNoTracking()
            .Where(d => d.OwnerIdentityId == ownerGuid)
            .ToListAsync(cancellationToken);

        var devicesByPeer = allDevices
            .GroupBy(d => d.RemotePeerId)
            .ToDictionary(g => g.Key, g => g.Select(d => new DeviceId(d.DeviceId)).ToList());

        var result = new List<PeerContact>(entities.Count);
        foreach (var entity in entities)
        {
            var peerId = new PublicIdentityId(entity.RemotePeerId);
            var pubKey = entity.PrimaryPublicKey != null && entity.PrimaryPublicKey.Length == 32
                ? IdentityKey.FromSpan(entity.PrimaryPublicKey)
                : null;
            var nickname = ContactNickname.Create(entity.Nickname, peerId);
            devicesByPeer.TryGetValue(entity.RemotePeerId, out var peerDevices);

            result.Add(PeerContact.Rehydrate(
                ownerId,
                peerId,
                nickname,
                (PeerTrustLevel)entity.TrustLevel,
                (ContactState)entity.State,
                entity.CreatedAtUtc,
                entity.LastSeenAtUtc,
                pubKey,
                peerDevices));
        }

        return result;
    }

    public async Task SaveAsync(PeerContact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);

        var ownerGuid = contact.OwnerIdentityId.Value;
        var remoteGuid = contact.RemotePeerId.Value;
        var pubKeyBytes = contact.PrimaryPublicKey?.Span.ToArray();
        var now = DateTimeOffset.UtcNow;

        var existing = await _dbContext.PeerContacts
            .FirstOrDefaultAsync(c => c.OwnerIdentityId == ownerGuid && c.RemotePeerId == remoteGuid, cancellationToken);

        if (existing == null)
        {
            var entity = new PeerContactEntity
            {
                OwnerIdentityId = ownerGuid,
                RemotePeerId = remoteGuid,
                Nickname = contact.Nickname.Value,
                TrustLevel = (int)contact.TrustLevel,
                State = (int)contact.State,
                PrimaryPublicKey = pubKeyBytes,
                CreatedAtUtc = contact.CreatedAtUtc,
                LastSeenAtUtc = contact.LastSeenAtUtc,
                UpdatedAtUtc = now
            };
            await _dbContext.PeerContacts.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.Nickname = contact.Nickname.Value;
            existing.TrustLevel = (int)contact.TrustLevel;
            existing.State = (int)contact.State;
            existing.PrimaryPublicKey = pubKeyBytes;
            existing.LastSeenAtUtc = contact.LastSeenAtUtc;
            existing.UpdatedAtUtc = now;
        }

        // Sync registered devices
        var existingDevices = await _dbContext.PeerContactDevices
            .Where(d => d.OwnerIdentityId == ownerGuid && d.RemotePeerId == remoteGuid)
            .ToListAsync(cancellationToken);

        var currentDeviceIds = contact.RegisteredDevices.Select(d => d.Value).ToHashSet();
        var existingDeviceIds = existingDevices.Select(d => d.DeviceId).ToHashSet();

        // Add new devices
        foreach (var deviceId in currentDeviceIds.Except(existingDeviceIds))
        {
            await _dbContext.PeerContactDevices.AddAsync(new PeerContactDeviceEntity
            {
                OwnerIdentityId = ownerGuid,
                RemotePeerId = remoteGuid,
                DeviceId = deviceId,
                RegisteredAtUtc = now
            }, cancellationToken);
        }

        // Remove deleted devices
        foreach (var toRemove in existingDevices.Where(d => !currentDeviceIds.Contains(d.DeviceId)))
        {
            _dbContext.PeerContactDevices.Remove(toRemove);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
