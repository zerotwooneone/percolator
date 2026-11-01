namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class RelaySwarmTrackerClient : IDistributedTrackerPort
{
    private readonly PercolatorDbContext _dbContext;

    public RelaySwarmTrackerClient(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<IReadOnlyList<DirectEndpointCandidate>> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        SwarmPeerRole role,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvousId);
        ArgumentNullException.ThrowIfNull(candidates);

        byte[] rendezvousBytes = rendezvousId.Span.ToArray();
        byte[] memberBytes = memberId.Value.ToByteArray();

        SwarmEndpointEntity? existing = await _dbContext.SwarmEndpoints
            .FirstOrDefaultAsync(e => e.RendezvousId == rendezvousBytes && e.PeerId == memberBytes, ct)
            .ConfigureAwait(false);

        string serializedCandidates = JsonSerializer.Serialize(candidates.Select(c => new
        {
            Host = c.Host.Value,
            Port = c.Port.Value,
            Kind = (int)c.Kind
        }));

        if (existing == null)
        {
            existing = new SwarmEndpointEntity
            {
                RendezvousId = rendezvousBytes,
                PeerId = memberBytes,
                Endpoint = serializedCandidates,
                PeerRole = (int)role,
                LastSeenUtc = DateTimeOffset.UtcNow
            };
            _dbContext.SwarmEndpoints.Add(existing);
        }
        else
        {
            existing.Endpoint = serializedCandidates;
            existing.PeerRole = (int)role;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        // Fetch other peers registered under the same rendezvous within the last 24 hours
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        List<SwarmEndpointEntity> otherEndpoints = await _dbContext.SwarmEndpoints
            .AsNoTracking()
            .Where(e => e.RendezvousId == rendezvousBytes && e.PeerId != memberBytes && e.LastSeenUtc >= cutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<DirectEndpointCandidate> resultCandidates = new();
        foreach (SwarmEndpointEntity other in otherEndpoints)
        {
            if (string.IsNullOrEmpty(other.Endpoint))
            {
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<CandidateDto>>(other.Endpoint);
                if (parsed != null)
                {
                    foreach (var c in parsed)
                    {
                        resultCandidates.Add(new DirectEndpointCandidate(
                            EndpointHost.Parse(c.Host),
                            PortNumber.FromInt(c.Port),
                            (DirectCandidateKind)c.Kind));
                    }
                }
            }
            catch
            {
                // Ignore corrupt serialized candidate entries
            }
        }

        return resultCandidates;
    }

    public async ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rendezvousId);

        byte[] rendezvousBytes = rendezvousId.Span.ToArray();
        byte[] memberBytes = memberId.Value.ToByteArray();

        SwarmEndpointEntity? existing = await _dbContext.SwarmEndpoints
            .FirstOrDefaultAsync(e => e.RendezvousId == rendezvousBytes && e.PeerId == memberBytes, ct)
            .ConfigureAwait(false);

        if (existing != null)
        {
            _dbContext.SwarmEndpoints.Remove(existing);
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class CandidateDto
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Kind { get; set; }
    }
}
