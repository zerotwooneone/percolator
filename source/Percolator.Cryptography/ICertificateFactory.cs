using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Cryptography;

/// <summary>
/// Defines a contract for a factory that can create or retrieve peer-specific TLS certificates.
/// This abstracts the underlying storage mechanism (e.g., file-based, in-memory).
/// </summary>
public interface ICertificateFactory
{
    /// <summary>
    /// Gets an existing TLS certificate for a peer or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="identityName">The name of the identity to associate with the certificate.</param>
    /// <param name="keyPair">The asymmetric key pair to use for the certificate's public/private key.</param>
    /// <param name="publicIdentitySigningKey">The public part of the peer's core identity key, to be embedded in a custom OID.</param>
    /// <returns>A valid X.509 certificate with a private key.</returns>
    X509Certificate2 GetOrCreatePeerCertificate(string identityName, AsymmetricAlgorithm keyPair, byte[] publicIdentitySigningKey);
}
