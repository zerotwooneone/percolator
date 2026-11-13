using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Percolator.Infrastructure.Vault;

namespace Percolator.Desktop.Common.Storage;

/// <summary>
/// SQLite realization of <see cref="IDesktopSettingsRepository"/> backed by an unencrypted
/// <c>desktop-settings.db</c> alongside the vault files. Contains only UI/launch preferences —
/// no secrets, so it must not be keyed.
/// </summary>
public sealed class SqliteDesktopSettingsRepository : IDesktopSettingsRepository
{
    private readonly string _connectionString;

    public SqliteDesktopSettingsRepository(VaultStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.BaseDirectory);
        _connectionString = $"Data Source={Path.Combine(options.BaseDirectory, "desktop-settings.db")}";
    }

    public async Task<DesktopIdentitySettingsEntity?> GetAsync(Guid identityId, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PreferredPort, LastBoundPort, AutoStart, SidebarOrder FROM IdentitySettings WHERE IdentityId = $id";
        command.Parameters.AddWithValue("$id", identityId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new DesktopIdentitySettingsEntity(
            identityId,
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt32(2) != 0,
            reader.GetInt32(3));
    }

    public async Task<IReadOnlyList<DesktopIdentitySettingsEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT IdentityId, PreferredPort, LastBoundPort, AutoStart, SidebarOrder FROM IdentitySettings ORDER BY SidebarOrder";

        var results = new List<DesktopIdentitySettingsEntity>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new DesktopIdentitySettingsEntity(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt32(3) != 0,
                reader.GetInt32(4)));
        }

        return results;
    }

    public async Task SaveAsync(DesktopIdentitySettingsEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO IdentitySettings (IdentityId, PreferredPort, LastBoundPort, AutoStart, SidebarOrder)
            VALUES ($id, $port, $last, $auto, $order)
            ON CONFLICT(IdentityId) DO UPDATE SET
                PreferredPort = $port, LastBoundPort = $last, AutoStart = $auto, SidebarOrder = $order
            """;
        command.Parameters.AddWithValue("$id", entity.IdentityId.ToString());
        command.Parameters.AddWithValue("$port", entity.PreferredPort);
        command.Parameters.AddWithValue("$last", entity.LastBoundPort.HasValue ? entity.LastBoundPort.Value : DBNull.Value);
        command.Parameters.AddWithValue("$auto", entity.AutoStart ? 1 : 0);
        command.Parameters.AddWithValue("$order", entity.SidebarOrder);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid identityId, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM IdentitySettings WHERE IdentityId = $id";
        command.Parameters.AddWithValue("$id", identityId.ToString());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey = $key";
        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
    }

    public async Task SetAppSettingAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await EnsureTableAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AppSettings (SettingKey, SettingValue) VALUES ($key, $value)
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = $value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS IdentitySettings (
                IdentityId TEXT PRIMARY KEY,
                PreferredPort INTEGER NOT NULL,
                LastBoundPort INTEGER NULL,
                AutoStart INTEGER NOT NULL DEFAULT 0,
                SidebarOrder INTEGER NOT NULL DEFAULT 0
            )
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var appSettingsCommand = connection.CreateCommand();
        appSettingsCommand.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AppSettings (
                SettingKey TEXT PRIMARY KEY,
                SettingValue TEXT NOT NULL
            )
            """;
        await appSettingsCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
