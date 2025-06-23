using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Percolator.Identity.Model;
using Identity = Percolator.Identity.Model.Identity;

namespace Percolator.Identity
{
    public class PersistentIdentityService : IIdentityService
    {
        private readonly IIdentityStore _identityStore;
        private readonly ICredentialService _credentialService;
        private readonly ICertificateOperations _certificateOperations;
        private readonly IKeyManagementService _keyManagementService;

        public PersistentIdentityService(
            IIdentityStore identityStore,
            ICredentialService credentialService,
            ICertificateOperations certificateOperations,
            IKeyManagementService keyManagementService)
        {
            _identityStore = identityStore;
            _credentialService = credentialService;
            _certificateOperations = certificateOperations;
            _keyManagementService = keyManagementService;
        }

        public async Task<IEnumerable<string>> ListIdentityNamesAsync()
        {
            return await _identityStore.ListIdentityNamesAsync();
        }

        public Task<Model.Identity?> GetIdentityAsync(string identityName)
        {
            return _identityStore.GetIdentityAsync(identityName);
        }

        public Task<bool> IdentityExistsAsync(string identityName)
        {
            return _identityStore.IdentityExistsAsync(identityName);
        }

        public async Task<X3dhKeys> GetIdentityKeysAsync(string identityName)
        {
            return await _keyManagementService.GetOrCreateKeysAsync(identityName);
        }

        public async Task<Model.Identity> CreateIdentityAsync(string identityName, string? nickname)
        {
            if (await _identityStore.IdentityExistsAsync(identityName))
            {
                throw new System.InvalidOperationException($"An identity with the name '{identityName}' already exists.");
            }

            var pfxPassword = _credentialService.GetOrCreatePfxPassword();

            // Create and export the certificate
            var newCert = _certificateOperations.CreateTlsCertificate(identityName);
            var pfxBytes = newCert.Export(X509ContentType.Pfx, pfxPassword);
            var pfxCertificate = new PfxCertificate(pfxBytes);

            // Ensure X3DH keys are created
            await _keyManagementService.GetOrCreateKeysAsync(identityName);

            // Create and store the identity
            var identity = new Model.Identity(identityName, pfxCertificate, newCert.Thumbprint, nickname);
            await _identityStore.StoreIdentityAsync(identity);

            return identity;
        }
    }
}
