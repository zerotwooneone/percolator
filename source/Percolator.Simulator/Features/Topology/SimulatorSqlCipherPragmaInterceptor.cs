using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Percolator.Domain.Security.Ports;

namespace Percolator.Simulator.Features.Topology;

/// <summary>
/// Simulator-optimized SQLCipher connection interceptor.
/// Avoids the 256,000-iteration PBKDF2-HMAC-SHA512 penalty (~500ms+) on every EF Core logical
/// connection checkout by configuring <c>PRAGMA kdf_iter = 1</c> (since each node's MasterKeyHandle
/// is already a 256-bit cryptographically random key) and only setting <c>journal_mode = WAL</c> once per database file.
/// </summary>
internal sealed class SimulatorSqlCipherPragmaInterceptor : DbConnectionInterceptor
{
    private readonly IMasterKeyHandle _keyHandle;
    private volatile bool _walInitialized;

    public SimulatorSqlCipherPragmaInterceptor(IMasterKeyHandle keyHandle)
    {
        _keyHandle = keyHandle ?? throw new ArgumentNullException(nameof(keyHandle));
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqliteConn)
        {
            ConfigureConnection(sqliteConn);
        }
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqliteConn)
        {
            ConfigureConnection(sqliteConn);
        }

        return Task.CompletedTask;
    }

    private void ConfigureConnection(SqliteConnection sqliteConn)
    {
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = "PRAGMA cipher_memory_security = ON;";
        cmd.ExecuteNonQuery();

        _keyHandle.ApplyKeyToConnection(sqliteConn);

        if (!_walInitialized)
        {
            cmd.CommandText = "PRAGMA kdf_iter = 1; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
            _walInitialized = true;
        }
        else
        {
            cmd.CommandText = "PRAGMA kdf_iter = 1; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
    }
}
