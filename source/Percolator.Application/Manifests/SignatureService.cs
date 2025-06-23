using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Manifests;

public class SignatureService : ISignatureService
{
    private readonly ILogger<SignatureService> _logger;
    private readonly IIdentityService _identityService;
    private readonly ICredentialService _credentialService;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public SignatureService(
        ILogger<SignatureService> logger,
        IIdentityService identityService,
        ICredentialService credentialService,
        ActiveIdentityContext activeIdentityContext)
    {
        _logger = logger;
        _identityService = identityService;
        _credentialService = credentialService;
        _activeIdentityContext = activeIdentityContext;
    }

    public Task<SignedManifest> SignManifestAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        if (_activeIdentityContext.Certificate is null)
        {
            throw new InvalidOperationException("Cannot sign manifest: Active identity is not loaded or does not have a certificate.");
        }

        return Task.Run(() =>
        {
            // Embed the signer's certificate in the manifest payload
            manifest.SignerCertificateDer = ByteString.CopyFrom(_activeIdentityContext.Certificate.RawData);

            var payloadBytes = manifest.ToByteArray();
            var signatureBytes = CryptoUtils.Sign(payloadBytes, _activeIdentityContext.Certificate.GetECDsaPrivateKey()!);

            var signedManifest = new SignedManifest
            {
                Manifest = manifest,
                Signature = ByteString.CopyFrom(signatureBytes)
            };

            _logger.LogInformation("Signed manifest with thumbprint {Thumbprint}", _activeIdentityContext.Certificate.Thumbprint);
            return signedManifest;
        }, cancellationToken);
    }

    public Task<bool> VerifyManifestAsync(SignedManifest signedManifest, CancellationToken cancellationToken)
    {
        if (signedManifest.Manifest is null || signedManifest.Manifest.SignerCertificateDer.IsEmpty)
        {
            _logger.LogWarning("Manifest verification failed: Signer certificate is missing.");
            return Task.FromResult(false);
        }

        return Task.Run(() =>
        {
            var certificate = X509CertificateLoader.LoadCertificate(signedManifest.Manifest.SignerCertificateDer.ToByteArray());

            var payloadBytes = signedManifest.Manifest.ToByteArray();
            var isValid = CryptoUtils.Verify(
                payloadBytes,
                signedManifest.Signature.ToByteArray(),
                certificate.GetECDsaPublicKey()!);

            _logger.LogInformation("Verified manifest signed by {Subject} ({Thumbprint}). IsValid: {IsValid}", certificate.Subject, certificate.Thumbprint, isValid);

            return isValid;
        }, cancellationToken);
    }
}
