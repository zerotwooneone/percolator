using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Identity;

public sealed class SelfIdentityQueries : ISelfIdentityQueries
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SelfIdentityQueries(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<byte[]?> GetRelayRootKeyAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        
        // Use AsNoTracking() for fast, no-tracking SQL projection
        var rootKey = await db.SelfIdentities
            .AsNoTracking()
            .Where(x => x.RelayDeliveryRootKey != null)
            .Select(x => x.RelayDeliveryRootKey)
            .FirstOrDefaultAsync(ct);

        return rootKey;
    }
}
