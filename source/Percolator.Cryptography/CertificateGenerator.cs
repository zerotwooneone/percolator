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
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(commonName);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                critical: true));

        request.CertificateExtensions.Add(sanBuilder.Build());

        // Add the custom extension for the peer identity key
        // The public key must be wrapped in an ASN.1 OCTET STRING for the client to parse it correctly.
        var asnWriter = new AsnWriter(AsnEncodingRules.DER);
        asnWriter.WriteOctetString(publicIdentitySigningKey);
        var extensionData = asnWriter.Encode();

        var peerIdentityExtension = new X509Extension(
            new Oid(Oids.PeerIdentityKey),
            extensionData,
            critical: false); // Not critical for standard validation, but essential for our app
        request.CertificateExtensions.Add(peerIdentityExtension);

        // CreateSelfSigned now correctly associates the private key.
        return request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
    }
}
