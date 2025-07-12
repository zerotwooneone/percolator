using System.Security.Cryptography;

namespace Percolator.Identity;

/// <summary>
/// Provides one-time pre-keys, ensuring each key is used only once.
/// </summary>
public interface IOneTimeKeyProvider
{
    /// <summary>
    /// Retrieves a one-time pre-key, consuming it so it cannot be used again.
    /// </summary>
    /// <returns>An ECDiffieHellman key, or null if no keys are available.</returns>
    ECDiffieHellman? PopOneTimeKey();
}
