using Percolator.Cryptography;
using Percolator.Identity;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application.Identity
{
    public class CertificateOperations : ICertificateOperations
    {
        public X509Certificate2 CreateSelfSignedCertificate(string commonName)
        {
            return CertificateGenerator.CreateSelfSignedCertificate(commonName);
        }

        public X509Certificate2 CreateTlsCertificate(string commonName)
        {
            return CertificateGenerator.CreateTlsCertificate(commonName);
        }
    }
}
