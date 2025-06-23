using System.Collections.Generic;
using System.Threading.Tasks;
using Percolator.Identity.Model;
using Identity = Percolator.Identity.Model.Identity;

namespace Percolator.Identity;

/// <summary>
/// Manages user identities, including creating, storing, and selecting them.
/// </summary>
public interface IIdentityService
{
    /// <summary>
    /// Lists the names of all available identities.
    /// </summary>
    /// <returns>A collection of identity names.</returns>
    Task<IEnumerable<string>> ListIdentityNamesAsync();

    /// <summary>
    /// Gets the identity for a specific identity name.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The identity.</returns>
    Task<Model.Identity?> GetIdentityAsync(string identityName);

    /// <summary>
    /// Gets the X3DH keys for a specific identity.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The X3DH keys for the specified identity.</returns>
    Task<X3dhKeys> GetIdentityKeysAsync(string identityName);

    /// <summary>
    /// Creates a new identity, including its certificate and X3DH keys.
    /// </summary>
    /// <param name="identityName">A friendly name for the new identity.</param>
    /// <param name="nickname">A nickname for the new identity.</param>
    /// <returns>The newly created identity.</returns>
    Task<Model.Identity> CreateIdentityAsync(string identityName, string? nickname);
}
