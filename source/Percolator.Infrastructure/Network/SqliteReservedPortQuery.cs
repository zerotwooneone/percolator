using Microsoft.EntityFrameworkCore;
using Percolator.Application.Network;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Network;

public class SqliteReservedPortQuery: IReservedPortQuery
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteReservedPortQuery(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<int>> GetReservedPortsAsync(CancellationToken ct)
    {
        return await _dbContext.SelfIdentities
            .Select(x => x.ListeningPort.Value) // Assuming EF Core maps the Value Object cleanly
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}