namespace Percolator.Application2.Ports;

/// <summary>
/// Driven port for instant emergency cryptographic shredding and secure data destruction under coercion.
/// </summary>
public interface IDuressWipeHandler
{
    /// <summary>
    /// Executes instant cryptographic shredding:
    /// Overwrites percolator.header with CSPRNG random bytes and forces hardware disk controller flush (&lt; 2 ms).
    /// Deletes percolator.header, percolator.db, percolator.db-wal, percolator.db-shm.
    /// Purges cached avatars, local attachments, and temporary staging stores.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteWipeAsync(CancellationToken ct = default);
}
