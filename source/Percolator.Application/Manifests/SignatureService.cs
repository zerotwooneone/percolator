using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using Cryptography_PublicKey = Percolator.Cryptography.PublicKey;
using Cryptography_Signature = Percolator.Cryptography.Signature;

namespace Percolator.Application.Manifests;

public class SignatureService : ISignatureService
{
    private readonly IIdentityService _identityService;
    private readonly ICredentialService _credentialService;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ISigningService _signingService;

    public SignatureService(
        IIdentityService identityService,
        ICredentialService credentialService,
        ActiveIdentityContext activeIdentityContext,
        ISigningService signingService)
    {
        _identityService = identityService;
        _credentialService = credentialService;
        _activeIdentityContext = activeIdentityContext;
        _signingService = signingService;
    }

    public async Task SignAsync(SignedManifest manifest)
    {
        if (string.IsNullOrEmpty(_activeIdentityContext.IdentityName))
        {
            throw new System.InvalidOperationException("Cannot sign manifest: No active identity.");
        }

        var identity = await _identityService.GetIdentityAsync(_activeIdentityContext.IdentityName);
        if (identity is null)
        {
            throw new System.InvalidOperationException($"Cannot sign manifest: Active identity '{_activeIdentityContext.IdentityName}' not found.");
        }

        var pfxPassword = _credentialService.GetOrCreatePfxPassword();
        using var certificate = X509CertificateLoader.LoadPkcs12(identity.PfxCertificate.Value, pfxPassword, X509KeyStorageFlags.Exportable);
        
        using var privateKey = certificate.GetECDsaPrivateKey()!;
        using var publicKey = certificate.GetECDsaPublicKey()!;
        var publicKeyBytes = publicKey.ExportSubjectPublicKeyInfo();

        var dataToSign = manifest.Manifest.ToByteArray();
        var signature = _signingService.Sign(dataToSign, privateKey);

        manifest.Signature = ByteString.CopyFrom(signature.Value);
        manifest.PublicKey = ByteString.CopyFrom(publicKeyBytes);
    }

    public Task<bool> VerifyAsync(SignedManifest manifest)
    {
        var publicKey = new Cryptography_PublicKey(manifest.PublicKey.ToByteArray());
        var signature = new Cryptography_Signature(manifest.Signature.ToByteArray());
        var dataToVerify = manifest.Manifest.ToByteArray();

        var isValid = _signingService.Verify(dataToVerify, signature, publicKey);
        return Task.FromResult(isValid);
    }
}
