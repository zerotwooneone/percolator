namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;

internal sealed class NoOpDatabaseMigrator : IDatabaseMigrator
{
    public Task<DomainResult> MigrateAsync(CancellationToken ct = default)
    {
        return Task.FromResult(DomainResult.Success());
    }
}
