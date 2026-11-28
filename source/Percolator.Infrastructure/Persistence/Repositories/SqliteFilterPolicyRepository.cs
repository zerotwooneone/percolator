namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteFilterPolicyRepository : IFileTransferFilterPolicyRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbContextFactory;

    public SqliteFilterPolicyRepository(IDbContextFactory<PercolatorDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        await using PercolatorDbContext dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        byte[] ownerBytes = policy.OwnerId.ToByteArray();
        FilterPolicyEntity? existing = await dbContext.FilterPolicies
            .FirstOrDefaultAsync(p => p.OwnerId == ownerBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            existing = new FilterPolicyEntity
            {
                OwnerId = ownerBytes
            };
            dbContext.FilterPolicies.Add(existing);
        }

        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        List<FilterPolicyBlockedPeerEntity> existingPeers = await dbContext.FilterPolicyBlockedPeers
            .Where(p => p.OwnerId == ownerBytes)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        dbContext.FilterPolicyBlockedPeers.RemoveRange(existingPeers);

        foreach (PublicIdentityId peer in policy.BlockedPeers)
        {
            dbContext.FilterPolicyBlockedPeers.Add(new FilterPolicyBlockedPeerEntity
            {
                OwnerId = ownerBytes,
                BlockedPeerId = peer.ToByteArray()
            });
        }

        List<FilterPolicyBlockedRelayEntity> existingRelays = await dbContext.FilterPolicyBlockedRelays
            .Where(r => r.OwnerId == ownerBytes)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        dbContext.FilterPolicyBlockedRelays.RemoveRange(existingRelays);

        foreach (RelayPipeEndpointUri relay in policy.BlockedRelays)
        {
            dbContext.FilterPolicyBlockedRelays.Add(new FilterPolicyBlockedRelayEntity
            {
                OwnerId = ownerBytes,
                RelayEndpointUri = relay.Value
            });
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        await using PercolatorDbContext dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        byte[] ownerBytes = ownerId.ToByteArray();
        FilterPolicyEntity? entity = await dbContext.FilterPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OwnerId == ownerBytes, ct)
            .ConfigureAwait(false);

        if (entity == null)
        {
            return FileTransferFilterPolicy.DefaultPermissive(ownerId);
        }

        List<FilterPolicyBlockedPeerEntity> blockedPeerEntities = await dbContext.FilterPolicyBlockedPeers
            .AsNoTracking()
            .Where(p => p.OwnerId == ownerBytes)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        HashSet<PublicIdentityId> blockedPeers = new();
        foreach (FilterPolicyBlockedPeerEntity p in blockedPeerEntities)
        {
            blockedPeers.Add(PublicIdentityId.FromBytes(p.BlockedPeerId));
        }

        List<FilterPolicyBlockedRelayEntity> blockedRelayEntities = await dbContext.FilterPolicyBlockedRelays
            .AsNoTracking()
            .Where(r => r.OwnerId == ownerBytes)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        HashSet<RelayPipeEndpointUri> blockedRelays = new();
        foreach (FilterPolicyBlockedRelayEntity r in blockedRelayEntities)
        {
            blockedRelays.Add(RelayPipeEndpointUri.Parse(r.RelayEndpointUri));
        }

        return new FileTransferFilterPolicy(ownerId, blockedPeers, blockedRelays);
    }
}
