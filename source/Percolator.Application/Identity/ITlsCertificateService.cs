using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application.Identity;

public interface ITlsCertificateService
{
    Task<X509Certificate2> GetOrCreateTlsCertificateAsync(string identityName);
}
