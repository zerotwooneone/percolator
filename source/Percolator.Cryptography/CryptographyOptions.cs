namespace Percolator.Cryptography;

/// <summary>
/// Configuration options for the Percolator.Cryptography library.
/// </summary>
public class CryptographyOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether diagnostic logging of cryptographic material hashes is enabled.
    /// This should be disabled in production environments to prevent sensitive information leakage.
    /// Default value is false (disabled) for security reasons.
    /// </summary>
    public bool EnableCryptographicMaterialLogging { get; set; } = false;

    /// <summary>
    /// Creates a new instance of <see cref="CryptographyOptions"/> with default settings optimized for security.
    /// </summary>
    public static CryptographyOptions CreateSecureDefault() => new CryptographyOptions();
    
    /// <summary>
    /// Creates a new instance of <see cref="CryptographyOptions"/> with settings optimized for development and debugging.
    /// WARNING: Do not use these settings in production environments.
    /// </summary>
    public static CryptographyOptions CreateDevelopmentDefault() => new CryptographyOptions
    {
        EnableCryptographicMaterialLogging = true
    };
}
