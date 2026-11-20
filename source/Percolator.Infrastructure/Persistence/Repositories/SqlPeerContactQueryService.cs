using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

public sealed class SqlPeerContactQueryService : IPeerContactQueryService
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SqlPeerContactQueryService(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    }

    private async Task<TResult> WithContextAsync<TResult>(Func<PercolatorDbContext, Task<TResult>> query)
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await query(db).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(Domain.Identities.PublicIdentityId ownerId, CancellationToken ct = default)
    {
        return WithContextAsync(async db =>
        {
            var ownerGuid = ownerId.Value;

            var contacts = await db.PeerContacts
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
                .ToListAsync(ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            return (IReadOnlyList<PeerContactSummaryReadModel>)contacts.Select(c =>
            {
                var isOnline = c.LastSeenAtUtc.HasValue && (now - c.LastSeenAtUtc.Value).TotalMinutes < 5;
                return new PeerContactSummaryReadModel(
                    new Domain.Identities.PublicIdentityId(c.RemotePeerId),
                    c.Nickname,
                    (PeerTrustLevel)c.TrustLevel,
                    (ContactState)c.State,
                    c.CreatedAtUtc,
                    isOnline);
            }).ToList();
        });
    }

    public Task<PeerContactDetailReadModel?> GetContactDetailAsync(Domain.Identities.PublicIdentityId ownerId, Domain.Identities.PublicIdentityId contactId, CancellationToken ct = default)
    {
        return WithContextAsync(async db =>
        {
            var ownerGuid = ownerId.Value;
            var contactGuid = contactId.Value;

            var entity = await db.PeerContacts
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.OwnerIdentityId == ownerGuid && c.RemotePeerId == contactGuid, ct).ConfigureAwait(false);

            if (entity == null)
            {
                return null;
            }

            var devices = await db.PeerContactDevices
                .AsNoTracking()
                .Where(d => d.OwnerIdentityId == ownerGuid && d.RemotePeerId == contactGuid)
                .Select(d => new DeviceId(d.DeviceId))
                .ToListAsync(ct).ConfigureAwait(false);

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
        });
    }
}
