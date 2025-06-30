using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Cryptography;

public static class CertificateGenerator
{
     public static X509Certificate2 CreateSelfSignedCertificate(string commonName = "localhost")
    {
        // The key object must not be disposed, as the returned certificate
        // object takes ownership of its lifetime.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        if (commonName != "localhost")
        {
            sanBuilder.AddDnsName(commonName);
        }

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));

        request.CertificateExtensions.Add(sanBuilder.Build());

        // CreateSelfSigned now correctly associates the private key.
        // There is no need to call CopyWithPrivateKey.
        return request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
    }

    public static X509Certificate2 CreateTlsCertificate(string commonName = "localhost")
    {
        // The key object must not be disposed, as the returned certificate
        // object takes ownership of its lifetime.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        if (commonName != "localhost")
        {
            sanBuilder.AddDnsName(commonName);
        }

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));

        request.CertificateExtensions.Add(sanBuilder.Build());

        // CreateSelfSigned now correctly associates the private key.
        // There is no need to call CopyWithPrivateKey.
        return request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
    }
}
