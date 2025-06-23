using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Percolator.Application.Security;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public class IdentityOrchestrator : IIdentityOrchestrator
{
    private readonly IIdentityService _identityService;
    private readonly ICredentialService _credentialService;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ITrustedPeerStore _trustedPeerStore;

    public IdentityOrchestrator(
        IIdentityService identityService,
        ICredentialService credentialService,
        ActiveIdentityContext activeIdentityContext,
        ITrustedPeerStore trustedPeerStore)
    {
        _identityService = identityService;
        _credentialService = credentialService;
        _activeIdentityContext = activeIdentityContext;
        _trustedPeerStore = trustedPeerStore;
    }

    public async Task LoadActiveIdentityAsync(string identityName)
    {
        var identity = await _identityService.GetIdentityAsync(identityName);
        if (identity is null)
        {
            throw new System.InvalidOperationException($"Identity '{identityName}' not found.");
        }

        X509Certificate2 certificate;
        try
        {
            var pfxPassword = _credentialService.GetOrCreatePfxPassword();
            certificate = X509CertificateLoader.LoadPkcs12(
                identity.PfxCertificate.Value,
                pfxPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"Failed to load identity '{identityName}'. The identity file may be corrupt or the credential store may have been changed.", ex);
        }

        var x3dhKeys = await _identityService.GetIdentityKeysAsync(identityName);

        _activeIdentityContext.IdentityName = identity.Name;
        _activeIdentityContext.Nickname = identity.Nickname;
        _activeIdentityContext.Certificate = certificate;

        var publicKeys = new Dictionary<string, byte[]>
        {
            { ActiveIdentityContext.SigningKey, certificate.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo() },
            { ActiveIdentityContext.IdentityKey, x3dhKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo() },
            { ActiveIdentityContext.PreKey, x3dhKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo() }
        };
        _activeIdentityContext.PublicKeys = publicKeys;

        var keyThumbprints = new Dictionary<string, string>
        {
            { ActiveIdentityContext.SigningKey, certificate.Thumbprint }
        };
        _activeIdentityContext.KeyThumbprints = keyThumbprints;

        // A node must always trust its own certificate.
        _trustedPeerStore.Add(certificate.Thumbprint);
    }
}
