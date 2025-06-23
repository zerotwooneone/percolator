using Percolator.Application.Identity;
using Percolator.Network;
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application.Network;

public class DiscoverySignatureProvider : IDiscoverySignatureProvider
{
    private readonly ActiveIdentityContext _activeIdentityContext;

    public DiscoverySignatureProvider(ActiveIdentityContext activeIdentityContext)
    {
        _activeIdentityContext = activeIdentityContext;
    }

    public byte[] GetPublicKeyCertificate()
    {
        if (_activeIdentityContext.Certificate is null)
        {
            throw new InvalidOperationException("Cannot get public key certificate: Active identity is not loaded.");
        }

        return _activeIdentityContext.Certificate.Export(X509ContentType.Cert);
    }

    public string GetThumbprint(byte[] publicKeyCertificate)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(publicKeyCertificate);
        return certificate.Thumbprint;
    }

    public byte[] Sign(byte[] data)
    {
        if (_activeIdentityContext.Certificate is null)
        {
            throw new InvalidOperationException("Cannot sign data: Active identity is not loaded.");
        }

        using var signingKey = _activeIdentityContext.Certificate.GetECDsaPrivateKey();
        if (signingKey is null)
        {
            throw new InvalidOperationException("Cannot sign data: Active identity's signing key is not available or is not an ECDsa key.");
        }

        return signingKey.SignData(data, HashAlgorithmName.SHA256);
    }

    public bool Verify(byte[] data, byte[] signature, byte[] publicKeyCertificate)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(publicKeyCertificate);
        using var publicKey = certificate.GetECDsaPublicKey();
        if (publicKey is null)
        {
            return false;
        }

        return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }
}
