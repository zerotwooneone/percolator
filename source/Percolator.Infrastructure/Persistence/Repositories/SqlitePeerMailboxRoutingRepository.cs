namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;
using Percolator.PluginSdk;

public sealed class SqlitePeerMailboxRoutingRepository : IPeerMailboxRoutingRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePeerMailboxRoutingRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<PeerMailboxRoutingCapability?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        var peerGuid = peerId.Value;

        var entity = await _dbContext.PeerMailboxRouting
            .AsNoTracking()
            .Where(r => r.PeerId == peerGuid)
            .OrderByDescending(r => r.LastSuccessAtUtc)
            .ThenByDescending(r => r.LastObservedAtUtc)
            .FirstOrDefaultAsync(ct);

        return entity != null ? MapEntityToDomain(entity) : null;
    }

    public async Task<IReadOnlyList<PeerMailboxRoutingCapability>> GetRoutesForPeerAsync(PublicIdentityId peerId, CancellationToken ct = default)
    {
        var peerGuid = peerId.Value;

        var entities = await _dbContext.PeerMailboxRouting
            .AsNoTracking()
            .Where(r => r.PeerId == peerGuid)
            .OrderByDescending(r => r.LastObservedAtUtc)
            .ToListAsync(ct);

        return entities.Select(MapEntityToDomain).ToList();
    }

    public async Task<PeerMailboxRoutingCapability?> GetBestRouteAsync(PublicIdentityId peerId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var peerGuid = peerId.Value;

        var entities = await _dbContext.PeerMailboxRouting
            .AsNoTracking()
            .Where(r => r.PeerId == peerGuid &&
                        (r.BackpressureCooldownUntilUtc == null || r.BackpressureCooldownUntilUtc <= nowUtc))
            .OrderByDescending(r => r.LastSuccessAtUtc)
            .ThenByDescending(r => r.LastObservedAtUtc)
            .ToListAsync(ct);

        var first = entities.FirstOrDefault();
        return first != null ? MapEntityToDomain(first) : null;
    }

    public async Task SaveAsync(PeerMailboxRoutingCapability capability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);

        var peerGuid = capability.PeerId.Value;
        var endpointStr = capability.RelayEndpoint?.ToString() ?? string.Empty;
        var routingGuid = capability.RoutingToken.Value;
        var deliveryBytes = capability.DeliveryToken.Span.ToArray();
        var now = DateTimeOffset.UtcNow;

        var existing = await _dbContext.PeerMailboxRouting
            .FirstOrDefaultAsync(r => r.PeerId == peerGuid && r.RelayEndpoint == endpointStr, ct);

        if (existing == null)
        {
            var entity = new PeerMailboxRoutingEntity
            {
                PeerId = peerGuid,
                RelayEndpoint = endpointStr,
                RoutingToken = routingGuid,
                DeliveryToken = deliveryBytes,
                LastObservedAtUtc = capability.LastObservedAtUtc,
                LastSuccessAtUtc = capability.LastSuccessAtUtc,
                BackpressureCooldownUntilUtc = capability.BackpressureCooldownUntilUtc,
                UpdatedAtUtc = now
            };
            await _dbContext.PeerMailboxRouting.AddAsync(entity, ct);
        }
        else
        {
            existing.RoutingToken = routingGuid;
            existing.DeliveryToken = deliveryBytes;
            existing.LastObservedAtUtc = capability.LastObservedAtUtc;
            existing.LastSuccessAtUtc = capability.LastSuccessAtUtc;
            existing.BackpressureCooldownUntilUtc = capability.BackpressureCooldownUntilUtc;
            existing.UpdatedAtUtc = now;
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task RecordBackpressureAsync(
        PublicIdentityId peerId,
        RelayEndpoint relayEndpoint,
        TimeSpan retryAfter,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayEndpoint);

        var peerGuid = peerId.Value;
        var endpointStr = relayEndpoint.ToString();

        var existing = await _dbContext.PeerMailboxRouting
            .FirstOrDefaultAsync(r => r.PeerId == peerGuid && r.RelayEndpoint == endpointStr, ct);

        if (existing != null)
        {
            existing.BackpressureCooldownUntilUtc = nowUtc.Add(retryAfter);
            existing.UpdatedAtUtc = nowUtc;
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task RecordSuccessAsync(
        PublicIdentityId peerId,
        RelayEndpoint relayEndpoint,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayEndpoint);

        var peerGuid = peerId.Value;
        var endpointStr = relayEndpoint.ToString();

        var existing = await _dbContext.PeerMailboxRouting
            .FirstOrDefaultAsync(r => r.PeerId == peerGuid && r.RelayEndpoint == endpointStr, ct);

        if (existing != null)
        {
            existing.LastSuccessAtUtc = nowUtc;
            existing.BackpressureCooldownUntilUtc = null;
            existing.UpdatedAtUtc = nowUtc;
            await _dbContext.SaveChangesAsync(ct);
        }
    }

    private static PeerMailboxRoutingCapability MapEntityToDomain(PeerMailboxRoutingEntity entity)
    {
        var peerId = new PublicIdentityId(entity.PeerId);
        var routingToken = new BlindedRoutingToken(entity.RoutingToken);
        var deliveryToken = DeliveryToken.FromSpan(entity.DeliveryToken);
        var relayEndpoint = !string.IsNullOrEmpty(entity.RelayEndpoint) ? RelayEndpoint.Parse(entity.RelayEndpoint) : null;

        return new PeerMailboxRoutingCapability(
            peerId,
            routingToken,
            deliveryToken,
            relayEndpoint,
            entity.LastObservedAtUtc,
            entity.LastSuccessAtUtc,
            entity.BackpressureCooldownUntilUtc);
    }
}
