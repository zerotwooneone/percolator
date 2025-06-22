using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Collections.Generic;
using System.Linq;

namespace Percolator.Identity
{
    public class PersistentIdentityService : IIdentityService
    {
        private readonly string _identitiesPath;
        private readonly ICredentialService _credentialService;
        private readonly ICertificateOperations _certificateOperations;
        private readonly IKeyManagementService _keyManagementService;

        public PersistentIdentityService(ICredentialService credentialService, ICertificateOperations certificateOperations, IKeyManagementService keyManagementService)
        {
            _credentialService = credentialService;
            _certificateOperations = certificateOperations;
            _keyManagementService = keyManagementService;
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
            _identitiesPath = Path.Combine(percolatorAppDataPath, "identities");
            Directory.CreateDirectory(_identitiesPath);
        }

        internal PersistentIdentityService(ICredentialService credentialService, ICertificateOperations certificateOperations, IKeyManagementService keyManagementService, string identitiesPath)
        {
            _credentialService = credentialService;
            _certificateOperations = certificateOperations;
            _keyManagementService = keyManagementService;
            _identitiesPath = identitiesPath;
            Directory.CreateDirectory(_identitiesPath);
        }

        public IEnumerable<string> ListIdentityNames()
        {
            return Directory.EnumerateFiles(_identitiesPath, "*.pfx")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)!;
        }

        public X509Certificate2 GetIdentityCertificate(string identityName)
        {
            var identityCertPath = Path.Combine(_identitiesPath, $"{identityName}.pfx");
            var pfxPassword = _credentialService.GetOrCreatePfxPassword();

            if (!File.Exists(identityCertPath))
            {
                throw new FileNotFoundException($"Identity '{identityName}' not found.", identityCertPath);
            }

            try
            {
                var pfxBytes = File.ReadAllBytes(identityCertPath);
                return X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException($"Error loading identity '{identityName}'. It may be corrupt or the credential store has changed.", ex);
            }
        }

        public X3dhKeys GetIdentityKeys(string identityName)
        {
            return _keyManagementService.GetOrCreateKeys(identityName);
        }

        public X509Certificate2 CreateIdentity(string identityName)
        {
            var identityCertPath = Path.Combine(_identitiesPath, $"{identityName}.pfx");
            if (File.Exists(identityCertPath))
            {
                throw new InvalidOperationException($"An identity with the name '{identityName}' already exists.");
            }

            var pfxPassword = _credentialService.GetOrCreatePfxPassword();

            // Create and save the certificate
            var newCert = _certificateOperations.CreateTlsCertificate(identityName);
            var pfxBytes = newCert.Export(X509ContentType.Pfx, pfxPassword);
            File.WriteAllBytes(identityCertPath, pfxBytes);
            SetFileSecurity(identityCertPath);

            // Create and save the corresponding X3DH keys
            _keyManagementService.GetOrCreateKeys(identityName);

            return X509CertificateLoader.LoadPkcs12(pfxBytes, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private void SetFileSecurity(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                fileSecurity.SetOwner(currentUser);
                var rule = new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow);
                fileSecurity.AddAccessRule(rule);
                fileInfo.SetAccessControl(fileSecurity);
            }
        }
    }
}
