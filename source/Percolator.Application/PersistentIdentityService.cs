using Percolator.Identity;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application
{
    public class PersistentIdentityService : IIdentityService
    {
        private readonly string _identitiesPath;
        private readonly ICredentialService _credentialService;

        public PersistentIdentityService(ICredentialService credentialService)
        {
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
                    Console.WriteLine($"Error loading identity '{identityName}'. It may be corrupt or the credential store has changed. {ex.Message}");
                    // Potentially delete and recreate, or throw.
                    throw;
                }
            }
            else
            {
                Console.WriteLine($"No identity '{identityName}' found. Generating a new one.");
                var newCert = CertificateGenerator.CreateSelfSignedCertificate(identityName);
                var pfxBytes = newCert.Export(X509ContentType.Pfx, pfxPassword);
                File.WriteAllBytes(identityCertPath, pfxBytes);
                return X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            }
        }
    }
}
