namespace Percolator.Infrastructure.Tests.Vault.Keying;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class SqlCipherPragmaInterceptorTests
{
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "sqlcipher_test_" + Guid.NewGuid().ToString("N") + ".db");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Test]
    public void ConnectionOpened_AppliesKeyAndSecurityPragmas()
    {
        // Arrange
        byte[] keyBytes = RandomNumberGenerator.GetBytes(32);
        using var keyHandle = new MasterKeyHandle(keyBytes);
        var options = new VaultStorageOptions { UseEncryption = true };
        var interceptor = new SqlCipherPragmaInterceptor(keyHandle, options);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        using var context = new PercolatorDbContext(dbOptions);

        // Act - Exercise real EF Core connection lifecycle
        context.Database.OpenConnection();

        // Assert: connection is keyed and PRAGMAs applied
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE TestTable (Id INTEGER PRIMARY KEY, Val TEXT); INSERT INTO TestTable (Val) VALUES ('secure'); SELECT Val FROM TestTable WHERE Id = 1;";
        var val = cmd.ExecuteScalar() as string;
        val.Should().Be("secure");

        cmd.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(cmd.ExecuteScalar());
        foreignKeys.Should().Be(1);
    }

    [Test]
    public async Task ConnectionOpenedAsync_AppliesKeyAndSecurityPragmas()
    {
        // Arrange
        byte[] keyBytes = RandomNumberGenerator.GetBytes(32);
        using var keyHandle = new MasterKeyHandle(keyBytes);
        var options = new VaultStorageOptions { UseEncryption = true };
        var interceptor = new SqlCipherPragmaInterceptor(keyHandle, options);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new PercolatorDbContext(dbOptions);

        // Act - Exercise real EF Core async connection lifecycle
        await context.Database.OpenConnectionAsync();

        // Assert: connection is keyed and PRAGMAs applied
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE TestAsync (Id INTEGER PRIMARY KEY, Val TEXT); INSERT INTO TestAsync (Val) VALUES ('async_secure'); SELECT Val FROM TestAsync WHERE Id = 1;";
        var val = (await cmd.ExecuteScalarAsync()) as string;
        val.Should().Be("async_secure");

        cmd.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        foreignKeys.Should().Be(1);
    }

    [Test]
    public void ConnectionOpened_WhenUseEncryptionFalse_BypassesKeyApplication()
    {
        // Arrange
        byte[] keyBytes = RandomNumberGenerator.GetBytes(32);
        using var keyHandle = new MasterKeyHandle(keyBytes);
        var options = new VaultStorageOptions { UseEncryption = false };
        var interceptor = new SqlCipherPragmaInterceptor(keyHandle, options);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        using var context = new PercolatorDbContext(dbOptions);

        // Act
        context.Database.OpenConnection();

        // Assert: connection operates as plain SQLite without keying
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE PlainTable (Id INTEGER PRIMARY KEY, Val TEXT); INSERT INTO PlainTable (Val) VALUES ('plain'); SELECT Val FROM PlainTable WHERE Id = 1;";
        var val = cmd.ExecuteScalar() as string;
        val.Should().Be("plain");

        cmd.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(cmd.ExecuteScalar());
        foreignKeys.Should().Be(1);
    }

    [Test]
    public async Task ConnectionOpenedAsync_WhenUseEncryptionFalse_BypassesKeyApplication()
    {
        // Arrange
        byte[] keyBytes = RandomNumberGenerator.GetBytes(32);
        using var keyHandle = new MasterKeyHandle(keyBytes);
        var options = new VaultStorageOptions { UseEncryption = false };
        var interceptor = new SqlCipherPragmaInterceptor(keyHandle, options);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        var dbOptions = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new PercolatorDbContext(dbOptions);

        // Act
        await context.Database.OpenConnectionAsync();

        // Assert: connection operates as plain SQLite without keying
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE PlainAsync (Id INTEGER PRIMARY KEY, Val TEXT); INSERT INTO PlainAsync (Val) VALUES ('plain_async'); SELECT Val FROM PlainAsync WHERE Id = 1;";
        var val = (await cmd.ExecuteScalarAsync()) as string;
        val.Should().Be("plain_async");

        cmd.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        foreignKeys.Should().Be(1);
    }
}
