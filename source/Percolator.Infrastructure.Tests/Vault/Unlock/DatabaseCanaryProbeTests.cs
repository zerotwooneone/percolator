namespace Percolator.Infrastructure.Tests.Vault.Unlock;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class DatabaseCanaryProbeTests
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
        _dbPath = Path.Combine(Path.GetTempPath(), "canary_test_" + Guid.NewGuid().ToString("N") + ".db");
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
    public async Task InvalidKey_TrapsSqliteErrorCode26()
    {
        // 1. Create encrypted database with Key A
        byte[] validKey = RandomNumberGenerator.GetBytes(32);
        using (var keyHandle = new MasterKeyHandle(validKey))
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            keyHandle.ApplyKeyToConnection(conn);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE CanaryTest (Id INTEGER PRIMARY KEY, Val TEXT); INSERT INTO CanaryTest VALUES (1, 'ok');";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        // 2. Open connection with wrong Key B and probe canary
        byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
        using (var wrongHandle = new MasterKeyHandle(wrongKey))
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            wrongHandle.ApplyKeyToConnection(conn);

            var probe = new DatabaseCanaryProbe();
            var result = await probe.ProbeAsync(conn);

            // SQLCipher fails to decrypt page 1 and SQLite returns error code 26 (SQLITE_NOTADB)
            result.Should().Be(CanaryProbeResult.FailedNotADatabase);
        }
    }

    [Test]
    public async Task ValidKey_ReturnsVerified()
    {
        byte[] validKey = RandomNumberGenerator.GetBytes(32);
        using (var keyHandle = new MasterKeyHandle(validKey))
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            keyHandle.ApplyKeyToConnection(conn);

            var probe = new DatabaseCanaryProbe();
            var result = await probe.ProbeAsync(conn);

            result.Should().Be(CanaryProbeResult.Verified);
        }
    }
}
