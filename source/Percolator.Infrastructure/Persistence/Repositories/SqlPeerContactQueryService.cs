namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

public sealed class SqlPeerContactQueryService : IPeerContactQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SqlPeerContactQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;

        var contacts = await _dbContext.PeerContacts
            .AsNoTracking()
            .Where(c => c.OwnerIdentityId == ownerGuid)
            .OrderBy(c => c.Nickname)
            .Select(c => new
            {
                c.RemotePeerId,
                c.Nickname,
                c.TrustLevel,
                c.State,
                c.CreatedAtUtc,
                c.LastSeenAtUtc
            })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return contacts.Select(c =>
        {
            var isOnline = c.LastSeenAtUtc.HasValue && (now - c.LastSeenAtUtc.Value).TotalMinutes < 5;
            return new PeerContactSummaryReadModel(
                new PublicIdentityId(c.RemotePeerId),
                c.Nickname,
                (PeerTrustLevel)c.TrustLevel,
                (ContactState)c.State,
                c.CreatedAtUtc,
                isOnline);
        }).ToList();
    }

    public async Task<PeerContactDetailReadModel?> GetContactDetailAsync(PublicIdentityId ownerId, PublicIdentityId contactId, CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var contactGuid = contactId.Value;

        var entity = await _dbContext.PeerContacts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerIdentityId == ownerGuid && c.RemotePeerId == contactGuid, ct);

        if (entity == null)
        {
            return null;
        }

        var devices = await _dbContext.PeerContactDevices
            .AsNoTracking()
            .Where(d => d.OwnerIdentityId == ownerGuid && d.RemotePeerId == contactGuid)
            .Select(d => new DeviceId(d.DeviceId))
            .ToListAsync(ct);

        var primaryKey = entity.PrimaryPublicKey != null && entity.PrimaryPublicKey.Length == 32
            ? IdentityKey.FromSpan(entity.PrimaryPublicKey)
            : IdentityKey.FromSpan(new byte[32]);

        return new PeerContactDetailReadModel(
            contactId,
            entity.Nickname,
            (PeerTrustLevel)entity.TrustLevel,
            (ContactState)entity.State,
            primaryKey,
            devices,
            entity.CreatedAtUtc,
            entity.LastSeenAtUtc ?? entity.CreatedAtUtc);
    }
}
