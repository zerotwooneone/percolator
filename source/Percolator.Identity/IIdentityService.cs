using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
    IEnumerable<string> ListIdentityNames();

    /// <summary>
    /// Gets the certificate for a specific identity.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The identity certificate.</returns>
    X509Certificate2 GetIdentityCertificate(string identityName);

    /// <summary>
    /// Gets the X3DH keys for a specific identity.
    /// </summary>
    /// <param name="identityName">The name of the identity.</param>
    /// <returns>The X3DH keys for the specified identity.</returns>
    X3dhKeys GetIdentityKeys(string identityName);

    /// <summary>
    /// Creates a new identity, including its certificate and X3DH keys.
    /// </summary>
    /// <param name="identityName">A friendly name for the new identity.</param>
    /// <returns>The newly created identity certificate.</returns>
    X509Certificate2 CreateIdentity(string identityName);
}
