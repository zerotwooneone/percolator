namespace Percolator.Infrastructure.Security
{
    /// <summary>
    /// Manages the encryption key for the local database.
    /// </summary>
    public interface IDatabaseEncryptionService
    {
        /// <summary>
        /// Gets the password for the SQLite database.
        /// If a password has not been created yet, a new one is generated and stored securely.
        /// </summary>
        /// <returns>The database password.</returns>
        string GetDatabasePassword();
    }
}
