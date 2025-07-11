using Percolator.Identity.Model;
using System.Security.Cryptography;

namespace Percolator.Identity;

/// <summary>
/// Manages the cryptographic keys (e.g., for X3DH) associated with user identities.
/// </summary>
public interface IKeyManagementService
{
    /// <summary>
    /// Retrieves the cryptographic keys for a given identity, creating them if they do not exist.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The cryptographic keys for the specified identity.</returns>
    Task<X3dhKeys> GetOrCreateKeysAsync(string identityName);
}
