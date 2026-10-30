namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRelayLedgerRepository : IRelayLedgerRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteRelayLedgerRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RelayGroupLedger?> GetByChannelIdAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        var channelIdGuid = channelId.Value;

        var entity = await _dbContext.RelayLedgerEpochs
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.ChannelId == channelIdGuid, cancellationToken);

        if (entity == null)
        {
            return null;
        }

        var tokens = await _dbContext.RelayLedgerRoutingTokens
            .AsNoTracking()
            .Where(t => t.ChannelId == channelIdGuid)
            .Select(t => new BlindedRoutingToken(t.RoutingToken))
            .ToListAsync(cancellationToken);

        var rosterBlob = EncryptedEntriesBlob.FromSpan(entity.EncryptedRosterBlob);
        var publicParams = ZkGroupPublicParams.FromSpan(entity.ZkPublicParams);

        return RelayGroupLedger.Rehydrate(
            channelId,
            new PublicIdentityId(entity.RelayIdentityId),
            new EpochNumber(entity.CurrentEpoch),
            rosterBlob,
            tokens,
            publicParams,
            entity.UpdatedAtUtc);
    }

    public async Task<IReadOnlyList<RelayGroupLedger>> GetAllForRelayAsync(PublicIdentityId relayIdentityId, CancellationToken cancellationToken = default)
    {
        var relayGuid = relayIdentityId.Value;

        var entities = await _dbContext.RelayLedgerEpochs
            .AsNoTracking()
            .Where(l => l.RelayIdentityId == relayGuid)
            .ToListAsync(cancellationToken);

        var result = new List<RelayGroupLedger>(entities.Count);
        foreach (var entity in entities)
        {
            var tokens = await _dbContext.RelayLedgerRoutingTokens
                .AsNoTracking()
                .Where(t => t.ChannelId == entity.ChannelId)
                .Select(t => new BlindedRoutingToken(t.RoutingToken))
                .ToListAsync(cancellationToken);

            var rosterBlob = EncryptedEntriesBlob.FromSpan(entity.EncryptedRosterBlob);
            var publicParams = ZkGroupPublicParams.FromSpan(entity.ZkPublicParams);

            result.Add(RelayGroupLedger.Rehydrate(
                new ChannelId(entity.ChannelId),
                relayIdentityId,
                new EpochNumber(entity.CurrentEpoch),
                rosterBlob,
                tokens,
                publicParams,
                entity.UpdatedAtUtc));
        }

        return result;
    }

    public async Task SaveAsync(RelayGroupLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var channelIdGuid = ledger.ChannelId.Value;
        var relayGuid = ledger.RelayIdentityId.Value;
        var rosterBytes = ledger.EncryptedRosterBlob.Span.ToArray();
        var paramsBytes = ledger.PublicParams.Span.ToArray();
        var now = DateTimeOffset.UtcNow;

        var existing = await _dbContext.RelayLedgerEpochs
            .FirstOrDefaultAsync(l => l.ChannelId == channelIdGuid, cancellationToken);

        if (existing == null)
        {
            var entity = new RelayLedgerEpochEntity
            {
                ChannelId = channelIdGuid,
                RelayIdentityId = relayGuid,
                CurrentEpoch = ledger.CurrentEpoch.Value,
                EncryptedRosterBlob = rosterBytes,
                ZkPublicParams = paramsBytes,
                UpdatedAtUtc = now
            };
            await _dbContext.RelayLedgerEpochs.AddAsync(entity, cancellationToken);
        }
        else
        {
            existing.RelayIdentityId = relayGuid;
            existing.CurrentEpoch = ledger.CurrentEpoch.Value;
            existing.EncryptedRosterBlob = rosterBytes;
            existing.ZkPublicParams = paramsBytes;
            existing.UpdatedAtUtc = now;
        }

        // Sync routing tokens
        var existingTokens = await _dbContext.RelayLedgerRoutingTokens
            .Where(t => t.ChannelId == channelIdGuid)
            .ToListAsync(cancellationToken);

        var currentTokens = ledger.ActiveRoutingTokens.ToDictionary(t => t.Value);
        var existingTokenGuids = existingTokens.ToDictionary(t => t.RoutingToken);

        // Add missing
        foreach (var (guid, token) in currentTokens)
        {
            if (!existingTokenGuids.ContainsKey(guid))
            {
                await _dbContext.RelayLedgerRoutingTokens.AddAsync(new RelayLedgerRoutingTokenEntity
                {
                    ChannelId = channelIdGuid,
                    RoutingToken = guid
                }, cancellationToken);
            }
        }

        // Delete removed
        foreach (var (guid, toRemove) in existingTokenGuids)
        {
            if (!currentTokens.ContainsKey(guid))
            {
                _dbContext.RelayLedgerRoutingTokens.Remove(toRemove);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
