using System.Security.Cryptography.X509Certificates;

namespace Percolator.Identity;

/// <summary>
/// Manages user identities, including creating, storing, and selecting them.
/// </summary>
public interface IIdentityService
{
    /// <summary>
    /// Gets the default certificate for the user's identity.
    /// </summary>
    /// <returns>The user's default identity certificate.</returns>
    X509Certificate2 GetDefaultIdentityCertificate();

    /// <summary>
    /// Creates a new identity certificate and stores it.
    /// </summary>
    /// <param name="identityName">A friendly name for the new identity.</param>
    /// <returns>The newly created identity certificate.</returns>
    X509Certificate2 CreateIdentity(string identityName);
}
