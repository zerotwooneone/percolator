namespace Percolator.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Vault;

public sealed class SqliteDatabaseMigrator : IDatabaseMigrator
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteDatabaseMigrator(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<DomainResult> MigrateAsync(CancellationToken ct = default)
    {
        try
        {
            var pending = _dbContext.Database.GetMigrations();
            if (pending.Any())
            {
                await _dbContext.Database.MigrateAsync(ct);
            }
            else
            {
                await _dbContext.Database.EnsureCreatedAsync(ct);
            }

            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("DATABASE_MIGRATION_FAILED", ex.Message));
        }
    }
}
