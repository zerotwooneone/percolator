using System.Security.Cryptography.X509Certificates;

namespace Percolator.Network;

/// <summary>
/// Defines operations for creating and managing TLS certificates for peers.
/// </summary>
public interface ITlsCertificateService
{
    Task<X509Certificate2> GetOrCreateTlsCertificateAsync(string identityName, byte[] publicIdentitySigningKey);
}
