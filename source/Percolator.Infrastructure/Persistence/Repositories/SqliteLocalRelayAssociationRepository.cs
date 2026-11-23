namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteLocalRelayAssociationRepository : ILocalRelayAssociationRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;

    public SqliteLocalRelayAssociationRepository(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<bool> IsAuthorizedAsync(BlindedRoutingToken routingToken, CancellationToken ct = default)
    {
        var routingBytes = routingToken.ToByteArray();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.LocalRelayAssociations
            .AsNoTracking()
            .AnyAsync(a => a.AuthorizedRoutingToken == routingBytes, ct)
            .ConfigureAwait(false);
    }

    public async Task AuthorizeAsync(string relayId, BlindedRoutingToken routingToken, CancellationToken ct = default)
    {
        var routingBytes = routingToken.ToByteArray();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.LocalRelayAssociations
            .FirstOrDefaultAsync(a => a.RelayId == relayId && a.AuthorizedRoutingToken == routingBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            var entity = new LocalRelayAssociationEntity
            {
                RelayId = relayId,
                AuthorizedRoutingToken = routingBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await db.LocalRelayAssociations.AddAsync(entity, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
