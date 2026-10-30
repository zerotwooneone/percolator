namespace Percolator.Infrastructure.Vault;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Percolator.Domain.Security.Ports;

public sealed class SqlCipherPragmaInterceptor : DbConnectionInterceptor
{
    private readonly IMasterKeyHandle _keyHandle;

    public SqlCipherPragmaInterceptor(IMasterKeyHandle keyHandle)
    {
        _keyHandle = keyHandle ?? throw new ArgumentNullException(nameof(keyHandle));
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqliteConn)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "PRAGMA cipher_memory_security = ON;";
            cmd.ExecuteNonQuery();

            _keyHandle.ApplyKeyToConnection(sqliteConn);

            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqliteConn)
        {
            await using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "PRAGMA cipher_memory_security = ON;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _keyHandle.ApplyKeyToConnection(sqliteConn);

            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
