using Percolator.Identity.Model;
using System.Security.Cryptography.X509Certificates;
using Percolator.Identity;

namespace Percolator.Infrastructure.Network.Certificates;

public interface ITransportCertificateProvider
{
    // Encapsulates generation, disk loading, and the 90-day rotation completely
    Task<X509Certificate2> GetValidCertificateAsync(SelfId selfId, CancellationToken ct);
}
