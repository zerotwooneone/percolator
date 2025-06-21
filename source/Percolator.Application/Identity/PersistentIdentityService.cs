using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Percolator.Identity;

namespace Percolator.Application.Identity
{
    public class PersistentIdentityService : IIdentityService
    {
        private readonly ILogger<PersistentIdentityService> _logger;
        private readonly string _identitiesPath;
        private readonly ICredentialService _credentialService;

        public PersistentIdentityService(ILogger<PersistentIdentityService> logger, ICredentialService credentialService)
        {
            _logger = logger;
            _credentialService = credentialService;
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _identitiesPath = Path.Combine(appDataPath, "Percolator", "identities");
            Directory.CreateDirectory(_identitiesPath);
        }

        public X509Certificate2 GetDefaultIdentityCertificate()
        {
            return GetOrCreateIdentity("default");
        }

        public X509Certificate2 CreateIdentity(string identityName)
        {
            // For now, creating a named identity is the same as getting or creating it.
            return GetOrCreateIdentity(identityName);
        }

        private X509Certificate2 GetOrCreateIdentity(string identityName)
        {
            var identityCertPath = Path.Combine(_identitiesPath, $"{identityName}.pfx");
            var pfxPassword = _credentialService.GetOrCreatePfxPassword();

            if (File.Exists(identityCertPath))
            {
                try
                {
                    var pfxBytes = File.ReadAllBytes(identityCertPath);
                    return X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
                }
                catch (CryptographicException ex)
                {
                    _logger.LogError(ex, "Error loading identity '{IdentityName}'. It may be corrupt or the credential store has changed.", identityName);
                    // Potentially delete and recreate, or throw.
                    throw;
                }
            }
            else
            {
                _logger.LogInformation("No identity '{IdentityName}' found. Generating a new one.", identityName);
                var newCert = CertificateGenerator.CreateSelfSignedCertificate(identityName);
                var pfxBytes = newCert.Export(X509ContentType.Pfx, pfxPassword);
                File.WriteAllBytes(identityCertPath, pfxBytes);
                return X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            }
        }
    }
}
