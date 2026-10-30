namespace Percolator.Infrastructure.Vault;

using Microsoft.Data.Sqlite;

public sealed class DatabaseCanaryProbe : IDatabaseCanaryProbe
{
    public async Task<CanaryProbeResult> ProbeAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
        try
        {
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return CanaryProbeResult.Verified;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
        {
            return CanaryProbeResult.FailedNotADatabase;
        }
        catch (SqliteException)
        {
            return CanaryProbeResult.Corrupted;
        }
    }
}
