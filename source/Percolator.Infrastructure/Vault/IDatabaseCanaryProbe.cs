namespace Percolator.Infrastructure.Vault;

using Microsoft.Data.Sqlite;

public enum CanaryProbeResult { Verified, FailedNotADatabase, Corrupted }

internal interface IDatabaseCanaryProbe
{
    Task<CanaryProbeResult> ProbeAsync(SqliteConnection connection, CancellationToken ct = default);
}
