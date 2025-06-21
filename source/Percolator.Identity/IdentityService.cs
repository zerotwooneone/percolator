using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Percolator.Identity;

public class IdentityService : IIdentityService
{
    private readonly ILogger<IdentityService> _logger;
    private readonly ICertificateOperations _certificateOperations;

    public IdentityService(ILogger<IdentityService> logger, ICertificateOperations certificateOperations)
    {
        _logger = logger;
        _certificateOperations = certificateOperations;
    }

    public X509Certificate2 GetDefaultIdentityCertificate()
    {
        return GetOrCreateIdentity("default");
    }

    public X509Certificate2 CreateIdentity(string name)
    {
        return GetOrCreateIdentity(name);
    }

    private X509Certificate2 GetOrCreateIdentity(string name)
    {
        // NOTE: This implementation is for demonstration and does not persist the certificate.
        // Use PersistentIdentityService for production scenarios.
        _logger.LogInformation("Creating new in-memory identity certificate for {name}", name);
        return _certificateOperations.CreateSelfSignedCertificate(name);
    }
}
