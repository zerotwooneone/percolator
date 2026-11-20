namespace Percolator.Infrastructure.Vault;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence;

/// <summary>
/// Bootstrap migrator used by the Stage-1 unlock coordinator. The keyed
/// <see cref="PercolatorDbContext"/> only exists inside the Stage-2 authenticated host,
/// which is not built until after this step — so this migrator constructs a temporary
/// context bound with the same SQLCipher interceptor configuration
/// ([DEC-PINNED-SQLITE-KEY], [DEC-CANARY-ERROR26-UNWRAP]).
/// </summary>
internal sealed class SqlCipherSchemaMigrator : IDatabaseMigrator
{
    private readonly VaultStorageOptions _options;

    public SqlCipherSchemaMigrator(VaultStorageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DomainResult> MigrateAsync(IMasterKeyHandle masterKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(masterKey);

        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite($"Data Source={_options.DatabaseFilePath};Mode=ReadWriteCreate")
            .AddInterceptors(new SqlCipherPragmaInterceptor(masterKey, _options))
            .Options;

        await using var context = new PercolatorDbContext(dbOptions);
        return await new SqliteDatabaseMigrator(context)
            .MigrateAsync(masterKey, ct)
            .ConfigureAwait(false);
    }
}
