using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Identity;

public static class CertificateExtensions
{
    public static ECDiffieHellman GetECDHKeyPair(this X509Certificate2 certificate)
    {
        var ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa == null)
        {
            throw new InvalidOperationException("Certificate does not contain an ECDSA private key.");
        }
        
        return ECDiffieHellman.Create(ecdsa.ExportParameters(true));
    }
}
