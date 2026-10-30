namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;

internal interface IDatabaseMigrator
{
    Task<DomainResult> MigrateAsync(CancellationToken ct = default);
}
