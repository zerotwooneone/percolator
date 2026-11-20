namespace Percolator.Infrastructure.Vault;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Percolator.Domain.Security.Ports;

public sealed class SqlCipherPragmaInterceptor : DbConnectionInterceptor
{
    private const string CipherMemorySecurityPragma = "PRAGMA cipher_memory_security = ON;";
    private const string StandardWalPragmas = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";

    private readonly IMasterKeyHandle _keyHandle;
    private readonly VaultStorageOptions _options;

    public SqlCipherPragmaInterceptor(IMasterKeyHandle keyHandle, VaultStorageOptions options)
    {
        _keyHandle = keyHandle ?? throw new ArgumentNullException(nameof(keyHandle));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqliteConn)
        {
            using var cmd = sqliteConn.CreateCommand();

            if (_options.UseEncryption)
            {
                cmd.CommandText = CipherMemorySecurityPragma;
                cmd.ExecuteNonQuery();

                _keyHandle.ApplyKeyToConnection(sqliteConn);
            }

            cmd.CommandText = StandardWalPragmas;
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

            if (_options.UseEncryption)
            {
                cmd.CommandText = CipherMemorySecurityPragma;
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                _keyHandle.ApplyKeyToConnection(sqliteConn);
            }

            cmd.CommandText = StandardWalPragmas;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
