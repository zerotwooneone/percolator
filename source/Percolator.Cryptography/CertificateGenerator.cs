using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Cryptography;

public static class CertificateGenerator
{
    public static X509Certificate2 CreateSelfSignedCertificate(string commonName = "localhost")
    {
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        if (commonName != "localhost")
        {
            sanBuilder.AddDnsName(commonName);
        }

        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest(
            $"cn={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(sanBuilder.Build());

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        // For Kestrel, it's often necessary to export and re-import the certificate
        // to include the private key in a way the server can use.
        // The password is null here; it will be applied by the calling service when saving the file.
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    public static X509Certificate2 CreateTlsCertificate(string commonName = "localhost")
    {
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        if (commonName != "localhost")
        {
            sanBuilder.AddDnsName(commonName);
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"cn={commonName}",
            ecdsa,
            HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        request.CertificateExtensions.Add(sanBuilder.Build());

        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}
