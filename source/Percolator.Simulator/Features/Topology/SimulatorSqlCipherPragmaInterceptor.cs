using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Percolator.Simulator.Features.Topology;

/// <summary>
/// Plain (unencrypted) SQLite connection interceptor for the Simulator.
/// Replaces the production <c>SqlCipherPragmaInterceptor</c> so simulated nodes store plain,
/// unencrypted SQLite databases without calling <c>sqlite3_key</c> or running PBKDF2 key derivation.
/// </summary>
internal sealed class SimulatorSqlCipherPragmaInterceptor : DbConnectionInterceptor
{
    private volatile bool _walInitialized;

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
        if (!_walInitialized)
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
            _walInitialized = true;
        }
        else
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
    }
}
