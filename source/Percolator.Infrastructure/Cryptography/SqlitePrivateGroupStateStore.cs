using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography;

public sealed class SqlitePrivateGroupStateStore : IPrivateGroupStateStore
{
    private readonly PercolatorDbContext _dbContext;

    public SqlitePrivateGroupStateStore(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [Obsolete("Remove me")]
    public Task<byte[]?> GetAsync(Guid groupId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Blob-based group state has been removed. Use normalized tables.");
    
    [Obsolete("Remove me")]
    public Task SaveAsync(Guid groupId, byte[] stateBlob, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Blob-based group state has been removed. Use normalized tables.");
}
