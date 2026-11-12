namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;

internal interface IDatabaseMigrator
{
    /// <summary>
    /// Applies the vault schema using the just-verified master key. Runs inside the unlock
    /// coordinator before any domain repositories are resolved ([DEC-CANARY-ERROR26-UNWRAP]).
    /// </summary>
    Task<DomainResult> MigrateAsync(IMasterKeyHandle masterKey, CancellationToken ct = default);
}
