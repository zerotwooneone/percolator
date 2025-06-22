using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Manifests;

public class SignatureService : ISignatureService
{
    private readonly IIdentityService _identityService;

    public SignatureService(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    public void Sign(SignedManifest manifest)
    {
        var identityName = _identityService.ListIdentityNames().FirstOrDefault();
        if (identityName is null)
        {
            throw new InvalidOperationException("Cannot sign manifest: No identities found.");
        }
        var certificate = _identityService.GetIdentityCertificate(identityName);
        var privateKey = certificate.GetRSAPrivateKey()!;
        var dataToSign = manifest.Manifest.ToByteArray();
        var signature = privateKey.SignData(dataToSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        manifest.Signature = ByteString.CopyFrom(signature);
        manifest.PublicKey = ByteString.CopyFrom(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
    }

    public bool Verify(SignedManifest manifest)
    {
        var certificate = X509CertificateLoader.LoadCertificate(manifest.PublicKey.ToByteArray());
        var publicKey = certificate.GetRSAPublicKey()!;
        var dataToVerify = manifest.Manifest.ToByteArray();
        var signature = manifest.Signature.ToByteArray();
        return publicKey.VerifyData(dataToVerify, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
