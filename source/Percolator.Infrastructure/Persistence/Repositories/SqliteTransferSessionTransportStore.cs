namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteTransferSessionTransportStore : ITransferSessionTransportStore
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbContextFactory;

    public SqliteTransferSessionTransportStore(IDbContextFactory<PercolatorDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    public async ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        Guid transferGuid = context.SessionId.Value;

        TransferSessionTransportContextEntity? existing = await db.TransferSessionTransportContexts
            .FirstOrDefaultAsync(t => t.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            existing = new TransferSessionTransportContextEntity
            {
                TransferId = transferGuid
            };
            db.TransferSessionTransportContexts.Add(existing);
        }

        existing.ChannelId = context.ChannelId.Value;
        existing.LocalIdentityId = context.LocalIdentityId.ToByteArray();
        existing.SwarmRendezvousId = context.SwarmRendezvousId.Span.ToArray();
        existing.EphemeralPeerId = context.EphemeralPeerId.Span.ToArray();
        existing.StreamAuthToken = context.StreamAuthToken.Span.ToArray();
        existing.WanPolicy = (int)context.WanPolicy;
        existing.LocalRootPath = context.LocalRootPath?.Value;
        existing.RelayEndpointUri = context.RelayRendezvous?.EndpointUri.Value;
        existing.RelayPipeToken = context.RelayRendezvous?.Token.Span.ToArray();
        existing.IsGroupTransfer = context.IsGroupTransfer;
        existing.SenderCertSha256 = context.SenderCertSha256?.Span.ToArray();

        var existingCandidates = await db.TransferSessionDirectCandidates
            .Where(c => c.TransferId == transferGuid)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        db.TransferSessionDirectCandidates.RemoveRange(existingCandidates);

        var seenEndpoints = new HashSet<(string Host, int Port)>();
        foreach (DirectEndpointCandidate c in context.DirectCandidates)
        {
            if (seenEndpoints.Add((c.Host.Value, c.Port.Value)))
            {
                db.TransferSessionDirectCandidates.Add(new TransferSessionDirectCandidateEntity
                {
                    TransferId = transferGuid,
                    Host = c.Host.Value,
                    Port = c.Port.Value,
                    Kind = (int)c.Kind
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        Guid transferGuid = sessionId.Value;

        TransferSessionTransportContextEntity? entity = await db.TransferSessionTransportContexts
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (entity == null ||
            entity.SwarmRendezvousId.Length != 32 ||
            entity.EphemeralPeerId.Length != 32 ||
            entity.StreamAuthToken.Length != 32 ||
            entity.LocalIdentityId.Length != 16)
        {
            return null;
        }

        List<TransferSessionDirectCandidateEntity> candidateEntities = await db.TransferSessionDirectCandidates
            .AsNoTracking()
            .Where(c => c.TransferId == transferGuid)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        RelayRendezvousDescriptor? relay = !string.IsNullOrWhiteSpace(entity.RelayEndpointUri) && entity.RelayPipeToken is { Length: 32 }
            ? new RelayRendezvousDescriptor(
                RelayPipeEndpointUri.Parse(entity.RelayEndpointUri),
                RelayPipeToken.FromBytes(entity.RelayPipeToken))
            : null;

        List<DirectEndpointCandidate> candidates = new(candidateEntities.Count);
        foreach (TransferSessionDirectCandidateEntity c in candidateEntities)
        {
            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse(c.Host),
                PortNumber.FromInt(c.Port),
                (DirectCandidateKind)c.Kind));
        }

        return new TransferSessionTransportContext(
            sessionId,
            Domain.Channels.ValueObjects.ChannelId.FromGuid(entity.ChannelId),
            Domain.Identities.PublicIdentityId.FromBytes(entity.LocalIdentityId),
            SwarmRendezvousId.FromBytes(entity.SwarmRendezvousId),
            EphemeralSwarmPeerId.FromBytes(entity.EphemeralPeerId),
            StreamAuthToken.FromBytes(entity.StreamAuthToken),
            (DirectWanPolicy)entity.WanPolicy,
            relay,
            candidates,
            string.IsNullOrWhiteSpace(entity.LocalRootPath) ? null : RootDirectoryPath.Parse(entity.LocalRootPath),
            entity.IsGroupTransfer,
            entity.SenderCertSha256 is { Length: 32 } ? CertSha256Fingerprint.FromBytes(entity.SenderCertSha256) : null);
    }

    public async ValueTask DeleteTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        await using PercolatorDbContext db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        Guid transferGuid = sessionId.Value;

        TransferSessionTransportContextEntity? entity = await db.TransferSessionTransportContexts
            .FirstOrDefaultAsync(t => t.TransferId == transferGuid, ct)
            .ConfigureAwait(false);

        if (entity != null)
        {
            db.TransferSessionTransportContexts.Remove(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
