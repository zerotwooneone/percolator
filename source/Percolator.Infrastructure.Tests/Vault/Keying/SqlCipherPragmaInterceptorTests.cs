namespace Percolator.Infrastructure.Tests.Vault.Keying;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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
        var interceptor = new SqlCipherPragmaInterceptor(keyHandle);

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        // Act
        interceptor.ConnectionOpened(connection, null!);

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
        var interceptor = new SqlCipherPragmaInterceptor(keyHandle);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        // Act
        await interceptor.ConnectionOpenedAsync(connection, null!);

        // Assert: connection is keyed and PRAGMAs applied
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE TestAsync (Id INTEGER PRIMARY KEY, Val TEXT); INSERT INTO TestAsync (Val) VALUES ('async_secure'); SELECT Val FROM TestAsync WHERE Id = 1;";
        var val = (await cmd.ExecuteScalarAsync()) as string;
        val.Should().Be("async_secure");

        cmd.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        foreignKeys.Should().Be(1);
    }
}
