using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Identity
{
    public class PersistentIdentityService : IIdentityService
    {
        private readonly string _identitiesPath;
        private readonly ICredentialService _credentialService;

        public PersistentIdentityService(ICredentialService credentialService)
        {
            _credentialService = credentialService;
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
            _identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
            Directory.CreateDirectory(_identitiesPath);
        }

        internal PersistentIdentityService(ICredentialService credentialService, string identitiesPath)
        {
            _credentialService = credentialService;
            _identitiesPath = identitiesPath;
            Directory.CreateDirectory(_identitiesPath);
        }

        public X509Certificate2 GetDefaultIdentityCertificate()
        {
            return GetOrCreateIdentity("default");
        }

        public X509Certificate2 CreateIdentity(string identityName)
        {
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
                    throw new CryptographicException($"Error loading identity '{identityName}'. It may be corrupt or the credential store has changed.", ex);
                }
            }
            else
            {
                var newCert = CertificateGenerator.CreateSelfSignedCertificate(identityName);
                var pfxBytes = newCert.Export(X509ContentType.Pfx, pfxPassword);
                File.WriteAllBytes(identityCertPath, pfxBytes);
                return X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            }
        }
    }
}
