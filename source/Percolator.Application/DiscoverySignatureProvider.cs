using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application;

public class DiscoverySignatureProvider : IDiscoverySignatureProvider
{
    private readonly IIdentityService _identityService;

    public DiscoverySignatureProvider(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    public byte[] GetPublicKeyCertificate()
    {
        var certificate = _identityService.GetDefaultIdentityCertificate();
        return certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
    }

    public string GetThumbprint(byte[] publicKeyCertificate)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(publicKeyCertificate);
        return certificate.Thumbprint;
    }

    public byte[] Sign(byte[] data)
    {
        var certificate = _identityService.GetDefaultIdentityCertificate();
        var privateKey = certificate.GetRSAPrivateKey()!;
        return privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public bool Verify(byte[] data, byte[] signature, byte[] publicKeyCertificate)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(publicKeyCertificate);
            using var publicKey = certificate.GetRSAPublicKey()!;
            return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            // Invalid certificate format
            return false;
        }
    }
}
