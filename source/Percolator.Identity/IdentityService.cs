using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;

namespace Percolator.Identity;

public class IdentityService : IIdentityService
{
    private readonly ILogger<IdentityService> _logger;

    public IdentityService(ILogger<IdentityService> logger)
    {
        _logger = logger;
    }

    public X509Certificate2 GetDefaultIdentityCertificate()
    {
        // TODO: Implement logic to load the user's default identity certificate.
        // For now, we can generate a new one for placeholder purposes.
        _logger.LogWarning("[IdentityService] WARNING: No default identity found. Generating a temporary one.");
        return CertificateGenerator.CreateSelfSignedCertificate("temp-default");
    }

    public X509Certificate2 CreateIdentity(string identityName)
    {
        // TODO: Implement logic to create and persist a new named identity.
        _logger.LogWarning("[IdentityService] WARNING: Creating a non-persistent identity named '{IdentityName}'.", identityName);
        return CertificateGenerator.CreateSelfSignedCertificate(identityName);
    }
}
