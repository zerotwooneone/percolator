using System.Security.Cryptography.X509Certificates;

namespace Percolator.Identity;

public class IdentityService : IIdentityService
{
    public X509Certificate2 GetDefaultIdentityCertificate()
    {
        // TODO: Implement logic to load the user's default identity certificate.
        // For now, we can generate a new one for placeholder purposes.
        Console.WriteLine("[IdentityService] WARNING: No default identity found. Generating a temporary one.");
        return CertificateGenerator.CreateSelfSignedCertificate();
    }

    public X509Certificate2 CreateIdentity(string identityName)
    {
        // TODO: Implement logic to create and persist a new named identity.
        Console.WriteLine($"[IdentityService] WARNING: Creating a non-persistent identity named '{identityName}'.");
        return CertificateGenerator.CreateSelfSignedCertificate();
    }
}
