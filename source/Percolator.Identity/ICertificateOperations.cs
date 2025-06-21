using System.Security.Cryptography.X509Certificates;

namespace Percolator.Identity
{
    public interface ICertificateOperations
    {
        X509Certificate2 CreateSelfSignedCertificate(string commonName);
    }
}
