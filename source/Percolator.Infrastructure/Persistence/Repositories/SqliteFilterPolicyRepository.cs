namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteFilterPolicyRepository : IFileTransferFilterPolicyRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteFilterPolicyRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        byte[] ownerBytes = policy.OwnerId.ToByteArray();
        FilterPolicyEntity? existing = await _dbContext.FilterPolicies
            .FirstOrDefaultAsync(p => p.OwnerId == ownerBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            existing = new FilterPolicyEntity
            {
                OwnerId = ownerBytes
            };
            _dbContext.FilterPolicies.Add(existing);
        }

        existing.BlockedPeersJson = JsonSerializer.Serialize(
            policy.BlockedPeers.Select(p => p.Value.ToString()));

        existing.BlockedRelaysJson = JsonSerializer.Serialize(
            policy.BlockedRelays.Select(r => r.Value));

        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<FileTransferFilterPolicy> GetPolicyAsync(Domain.Identities.PublicIdentityId ownerId, CancellationToken ct = default)
    {
        byte[] ownerBytes = ownerId.ToByteArray();
        FilterPolicyEntity? entity = await _dbContext.FilterPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OwnerId == ownerBytes, ct)
            .ConfigureAwait(false);

        if (entity == null)
        {
            return FileTransferFilterPolicy.DefaultPermissive(ownerId);
        }

        HashSet<Domain.Identities.PublicIdentityId> blockedPeers = new();
        if (!string.IsNullOrEmpty(entity.BlockedPeersJson))
        {
            var peerGuids = JsonSerializer.Deserialize<List<string>>(entity.BlockedPeersJson);
            if (peerGuids != null)
            {
                foreach (string guidStr in peerGuids)
                {
                    if (Guid.TryParse(guidStr, out Guid g))
                    {
                        blockedPeers.Add(Domain.Identities.PublicIdentityId.FromGuid(g));
                    }
                }
            }
        }

        HashSet<RelayPipeEndpointUri> blockedRelays = new();
        if (!string.IsNullOrEmpty(entity.BlockedRelaysJson))
        {
            var relayUris = JsonSerializer.Deserialize<List<string>>(entity.BlockedRelaysJson);
            if (relayUris != null)
            {
                foreach (string uriStr in relayUris)
                {
                    blockedRelays.Add(RelayPipeEndpointUri.Parse(uriStr));
                }
            }
        }

        return new FileTransferFilterPolicy(ownerId, blockedPeers, blockedRelays);
    }
}
