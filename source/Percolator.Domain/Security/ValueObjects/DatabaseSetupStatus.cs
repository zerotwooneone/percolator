namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Represents the initialization and security state of the underlying SQLite database.
/// </summary>
public enum DatabaseSetupStatus
{
    /// <summary>
    /// Database header is missing or unconfigured. Application must present first-time setup.
    /// </summary>
    Uninitialized = 0,

    /// <summary>
    /// Database exists and is encrypted on disk. Application requires valid passphrase to unlock.
    /// </summary>
    Locked = 1,

    /// <summary>
    /// Master key has been validated and the database is actively unlocked.
    /// </summary>
    Unlocked = 2
}
