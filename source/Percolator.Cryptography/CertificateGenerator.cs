using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Cryptography;

public static class CertificateGenerator
{
    public static X509Certificate2 CreateTlsCertificate(ECDsa key, string commonName, byte[] publicIdentitySigningKey)
    {
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName(commonName);

        var asnWriter = new AsnWriter(AsnEncodingRules.DER);
        asnWriter.WriteOctetString(publicIdentitySigningKey);
        var encodedPublicKey = asnWriter.Encode();

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(Oids.ServerAuthentication) }, 
                critical: true));
        
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509Extension(Oids.PeerIdentityKey, encodedPublicKey, false));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
