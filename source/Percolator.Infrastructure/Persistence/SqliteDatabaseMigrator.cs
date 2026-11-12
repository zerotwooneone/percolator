namespace Percolator.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Vault;

public sealed class SqliteDatabaseMigrator : IDatabaseMigrator
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteDatabaseMigrator(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="masterKey"/> is unused: this scoped instance's DbContext is already
    /// key-pinned through the Stage-2 <see cref="SqlCipherPragmaInterceptor"/>.
    /// </remarks>
    public async Task<DomainResult> MigrateAsync(IMasterKeyHandle masterKey, CancellationToken ct = default)
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
                // EnsureCreated() cannot be used: it no-ops when ANY table exists, and the
                // vault always contains __percolator_vault written during initialization —
                // so schema would silently never apply. Generate the model DDL and apply it
                // idempotently via CREATE ... IF NOT EXISTS instead.
                var script = _dbContext.Database.GenerateCreateScript();
                var idempotent = Regex.Replace(
                    script,
                    @"CREATE (UNIQUE INDEX|INDEX|TABLE)\s",
                    "CREATE $1 IF NOT EXISTS ");
                await _dbContext.Database.ExecuteSqlRawAsync(idempotent, ct);
            }

            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("DATABASE_MIGRATION_FAILED", ex.Message));
        }
    }
}
