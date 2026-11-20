namespace Percolator.Infrastructure.Tests.Persistence.Core;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Persistence;

[TestFixture]
public class SqliteDatabaseMigratorTests
{
    [Test]
    public async Task MigrateAsync_AppliesInitialSchema()
    {
        // Arrange
        SQLitePCL.Batteries_V2.Init();
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        using var dbContext = new PercolatorDbContext(options);
        var migrator = new SqliteDatabaseMigrator(dbContext);

        // Act
        var result = await migrator.MigrateAsync(masterKey: null!);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Verify table existence in sqlite_master
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'identity_profiles';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(1);
    }

    [Test]
    public async Task MigrateAsync_PartialSchema_CreatesMissingModelTables()
    {
        // Regression: the unlock coordinator creates __percolator_vault before migration
        // runs, so EnsureCreated() — which no-ops when any table exists — silently skipped
        // schema creation on every real vault ([DEC-CANARY-ERROR26-UNWRAP]).
        SQLitePCL.Batteries_V2.Init();
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        await using (var seed = conn.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE __percolator_vault (id INTEGER PRIMARY KEY);";
            await seed.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        using var dbContext = new PercolatorDbContext(options);
        var migrator = new SqliteDatabaseMigrator(dbContext);

        var result = await migrator.MigrateAsync(masterKey: null!);

        result.IsSuccess.Should().BeTrue();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'identity_profiles';";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(1, "model tables must be created even when non-model tables already exist");
    }

    [Test]
    public async Task MigrateAsync_CreatesVaultSettingsTables_AndIsIdempotent()
    {
        // STOR-01: the plaintext desktop-settings store moves into the encrypted vault as
        // EF entities; the no-migrations DDL path must create the new tables on unlock
        // (existing vaults) and on fresh-vault initialization, and re-running must be safe.
        SQLitePCL.Batteries_V2.Init();
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(conn)
            .Options;

        using var dbContext = new PercolatorDbContext(options);
        var migrator = new SqliteDatabaseMigrator(dbContext);

        (await migrator.MigrateAsync(masterKey: null!)).IsSuccess.Should().BeTrue();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM sqlite_master
            WHERE type = 'table' AND name IN ('identity_settings', 'app_settings');
            """;
        Convert.ToInt32(await cmd.ExecuteScalarAsync()).Should().Be(2,
            "both vault-settings tables must be created by the model-DDL path");

        // Second run — the CREATE ... IF NOT EXISTS rewrite must make this a no-op.
        (await migrator.MigrateAsync(masterKey: null!)).IsSuccess.Should().BeTrue();
    }
}
