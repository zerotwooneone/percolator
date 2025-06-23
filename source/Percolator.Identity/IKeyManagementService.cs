using System.Threading.Tasks;

namespace Percolator.Identity;

/// <summary>
/// Manages the cryptographic keys (e.g., for X3DH) associated with user identities.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>
    /// Retrieves the X3DH keys for a given identity, creating them if they do not exist.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The X3DH keys for the specified identity.</returns>
    Task<X3dhKeys> GetOrCreateKeysAsync(string identityName);

    /// <param name="identityName">The name of the identity.</param>
    /// <returns>A KeyContainer containing the identity key, signed pre-key, and one-time pre-key.</returns>
    Task<X3dhKeys> GetIdentityKeysAsync(string identityName);
}
