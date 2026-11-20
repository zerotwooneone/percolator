namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;

internal sealed class NoOpDatabaseMigrator : IDatabaseMigrator
{
    public Task<DomainResult> MigrateAsync(IMasterKeyHandle masterKey, CancellationToken ct = default)
    {
        return Task.FromResult(DomainResult.Success());
    }
}
