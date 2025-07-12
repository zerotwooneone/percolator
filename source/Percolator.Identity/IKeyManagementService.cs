namespace Percolator.Identity;

/// <summary>
/// Manages the cryptographic keys (e.g., for X3DH) associated with user identities.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>
    /// Retrieves the cryptographic keys for a given identity.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The cryptographic keys for the specified identity.</returns>
    Task<X3dhKeys?> GetKeysAsync(string identityName);

    /// <summary>
    /// Creates the cryptographic keys for a given identity.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The newly created cryptographic keys for the specified identity.</returns>
    Task<X3dhKeys> CreateKeysAsync(string identityName);
}
