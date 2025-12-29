using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using Percolator.Application.Cryptography;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Persistence;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Infrastructure.Application;

public class PendingHandshakeQueries : IPendingHandshakeQueries
{
    private readonly PercolatorDbContext _dbContext;
    private readonly IClock _clock;

    public PendingHandshakeQueries(PercolatorDbContext dbContext,
        IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }
    public async IAsyncEnumerable<PendingHandshake> EnumerateOpenAsync(CancellationToken cancellationToken=default)
    {
        var nowUtc = _clock.UtcNow;

        var sqlQuery =
            from ps in _dbContext.PendingSessions.AsNoTracking()
            where ps.State == (int)ApprovalState.AwaitingApproval
            join pi in _dbContext.PeerIdentities.AsNoTracking()
                on ps.RemotePeerId equals pi.PeerId into g
            from pi in g.DefaultIfEmpty()
            select new
            {
                Id = ps.Id,                 // primitive
                RemotePeerId = ps.RemotePeerId,
                IsRelayed = ps.IsRelayed,
                // keep raw display name-ish field (primitive or nullable) from DB
                PeerDisplayName = pi == null ? null : pi.Name,
                RequestCorrelationId = ps.RequestCorrelationId,
                InviterIdentityKey = ps.InviterIdentityKey,
                CreatedAtUtc = ps.CreatedAtUtc,
                ExpiresAtUtc = ps.ExpiresAtUtc
            };

        var rows = await sqlQuery.ToListAsync(cancellationToken).ConfigureAwait(false);

        var relayedPeerIds = rows
            .Where(r => r.IsRelayed)
            .Select(r => r.RemotePeerId)
            .Distinct()
            .ToList();

        var relayByRemote = new Dictionary<Guid, Guid>();
        if (relayedPeerIds.Count > 0)
        {
            var relayLinks = await _dbContext.PeerRoutingRelays
                .AsNoTracking()
                .Where(r => relayedPeerIds.Contains(r.PeerId))
                .OrderByDescending(r => r.LastSeenUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var link in relayLinks)
            {
                if (!relayByRemote.ContainsKey(link.PeerId))
                {
                    relayByRemote[link.PeerId] = link.RelayPeerId;
                }
            }
        }

        var relayPeerIds = relayByRemote.Values.Distinct().ToList();

        var relayNameById = new Dictionary<Guid, string>();
        if (relayPeerIds.Count > 0)
        {
            var relays = await _dbContext.PeerIdentities
                .AsNoTracking()
                .Where(p => relayPeerIds.Contains(p.PeerId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var p in relays)
            {
                relayNameById[p.PeerId] = p.Name;
            }
        }

        var relayEndpointById = new Dictionary<Guid, string>();
        if (relayPeerIds.Count > 0)
        {
            var eps = await _dbContext.PeerRoutingGrpcEndPoints
                .AsNoTracking()
                .Where(e => relayPeerIds.Contains(e.PeerId))
                .OrderByDescending(e => e.LastSeenUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var ep in eps)
            {
                if (!relayEndpointById.ContainsKey(ep.PeerId))
                {
                    relayEndpointById[ep.PeerId] = $"{ep.Host}:{ep.Port}";
                }
            }
        }

        foreach (var row in rows
                     .Where(r => r.ExpiresAtUtc == null || r.ExpiresAtUtc > nowUtc)
                     .OrderByDescending(r => r.CreatedAtUtc))
        {
            var peerName = row.PeerDisplayName
                           ?? row.RemotePeerId.ToString()[..8];

            if (string.IsNullOrWhiteSpace(row.RequestCorrelationId)
                || !Guid.TryParse(row.RequestCorrelationId, out var correlationGuid)
                || correlationGuid == Guid.Empty)
            {
                throw new InvalidOperationException("Pending session row has missing/invalid request_correlation_id. Purge outdated pending sessions.");
            }

            string? inviterFingerprintHex = null;
            if (row.InviterIdentityKey is not null && row.InviterIdentityKey.Length > 0)
            {
                inviterFingerprintHex = Convert.ToHexString(SHA256.HashData(row.InviterIdentityKey));
            }

            PeerId? relayPeerId = null;
            string? relayPeerName = null;
            string? relayEndpoint = null;
            if (row.IsRelayed && relayByRemote.TryGetValue(row.RemotePeerId, out var relayId))
            {
                relayPeerId = new PeerId(relayId);
                relayPeerName = relayNameById.TryGetValue(relayId, out var name)
                    ? name
                    : relayId.ToString()[..8];
                relayEndpoint = relayEndpointById.TryGetValue(relayId, out var ep)
                    ? ep
                    : null;
            }

            yield return new PendingHandshake
            {
                Id = new PendingSessionId(row.Id),
                RemotePeer = new PeerId(row.RemotePeerId),
                PeerName = peerName,
                RequestCorrelationId = new RequestCorrelationId(correlationGuid),
                InviterFingerprintHex = inviterFingerprintHex,
                CreatedAtUtc = row.CreatedAtUtc,
                ExpiresAtUtc = row.ExpiresAtUtc,

                IsRelayed = row.IsRelayed,
                RelayPeer = relayPeerId,
                RelayPeerName = relayPeerName,
                RelayEndpoint = relayEndpoint
            };
        }
    }
}