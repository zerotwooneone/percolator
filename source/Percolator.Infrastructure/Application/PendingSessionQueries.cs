using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Cryptography;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Application;

public sealed class PendingSessionQueries : IPendingSessionQueries
{
    private readonly PercolatorDbContext _dbContext;

    public PendingSessionQueries(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PendingSessionId?> TryGetByRequestCorrelationIdAsync(
        RequestCorrelationId requestCorrelationId,
        CancellationToken cancellationToken = default)
    {
        var correlation = requestCorrelationId.Value.ToString();

        var row = await _dbContext.PendingSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.RequestCorrelationId == correlation, cancellationToken)
            .ConfigureAwait(false);

        if (row is null) return null;

        return new PendingSessionId(row.Id);
    }
}
