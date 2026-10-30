namespace Percolator.Infrastructure.Tests.Persistence.Core;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class SqliteDatabaseMigratorTests
{
    [Test]
    public async Task MigrateAsync_AppliesInitialSchema()
    {
        // Arrange
        SQLitePCL.Batteries_V2.Init();
        using var handle = MasterKeyHandle.GenerateRandom();
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        using var dbContext = new PercolatorDbContext(options, handle);
        var migrator = new SqliteDatabaseMigrator(dbContext);

        // Act
        var result = await migrator.MigrateAsync();

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify table existence in sqlite_master
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'identity_profiles';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(1);
    }
}
