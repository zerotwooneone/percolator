namespace Percolator.Infrastructure.Transport;

using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

public interface ITransportCertificateProvider : IDisposable
{
    ValueTask<X509Certificate2> GetOrCreateServerCertificateAsync(CancellationToken ct = default);
    ValueTask<X509Certificate2> RotateCertificateAsync(CancellationToken ct = default);
}
