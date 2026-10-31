namespace Percolator.Infrastructure.Transport;

using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public sealed class InMemoryTransportCertificateProvider : ITransportCertificateProvider
{
    private readonly Lazy<X509Certificate2> _certificate;

    public InMemoryTransportCertificateProvider()
    {
        _certificate = new Lazy<X509Certificate2>(CreateEphemeralCertificate);
    }

    public X509Certificate2 GetCertificate() => _certificate.Value;

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=PercolatorEphemeralPeer", ecdsa, HashAlgorithmName.SHA256);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        req.CertificateExtensions.Add(sanBuilder.Build());

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));

        return cert;
    }
}
