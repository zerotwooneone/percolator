namespace Percolator.Infrastructure.Tests.Maintenance;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset initial)
    {
        _utcNow = initial;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
    }
}

internal sealed class TestDbContextFactory : IDbContextFactory<PercolatorDbContext>
{
    private readonly SqliteTestDb _testDb;

    public TestDbContextFactory(SqliteTestDb testDb)
    {
        _testDb = testDb;
    }

    public PercolatorDbContext CreateDbContext() => _testDb.CreateNewContext();

    public Task<PercolatorDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_testDb.CreateNewContext());
}
