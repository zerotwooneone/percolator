namespace Percolator.Infrastructure.Tests.Persistence.TestSupport;

using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Vault;

public sealed class SqliteTestDb : IDisposable
{
    public SqliteConnection Connection { get; }
    public MasterKeyHandle MasterKeyHandle { get; }
    public PercolatorDbContext DbContext { get; }

    public SqliteTestDb()
    {
        SQLitePCL.Batteries_V2.Init();
        MasterKeyHandle = MasterKeyHandle.GenerateRandom();
        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(Connection)
            .ReplaceService<IModelCacheKeyFactory, PercolatorModelCacheKeyFactory>()
            .Options;

        DbContext = new PercolatorDbContext(options, MasterKeyHandle);
        DbContext.Database.EnsureCreated();
    }

    public PercolatorDbContext CreateNewContext()
    {
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(Connection)
            .ReplaceService<IModelCacheKeyFactory, PercolatorModelCacheKeyFactory>()
            .Options;

        return new PercolatorDbContext(options, MasterKeyHandle);
    }

    public void Dispose()
    {
        DbContext.Dispose();
        Connection.Dispose();
        MasterKeyHandle.Dispose();
    }
}
