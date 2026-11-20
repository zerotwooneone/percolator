namespace Percolator.Infrastructure.Vault;

using Percolator.Domain.Common;

internal interface IHeaderStorage
{
    Task<DomainResult<DatabaseHeader>> ReadHeaderAsync(CancellationToken ct = default);
    Task<DomainResult> WriteHeaderAsync(DatabaseHeader header, CancellationToken ct = default);
    Task<DomainResult> OverwriteAndWipeAsync(CancellationToken ct = default);
}
