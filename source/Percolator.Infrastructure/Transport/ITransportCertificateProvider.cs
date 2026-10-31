namespace Percolator.Infrastructure.Transport;

using System.Security.Cryptography.X509Certificates;

public interface ITransportCertificateProvider
{
    X509Certificate2 GetCertificate();
}
