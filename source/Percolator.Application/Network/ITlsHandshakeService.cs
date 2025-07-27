using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Service for handling TLS handshakes and certificate operations
    /// </summary>
    public interface ITlsHandshakeService
    {
        /// <summary>
        /// Captures a remote certificate through direct TLS handshake
        /// </summary>
        /// <param name="endpoint">The endpoint to connect to</param>
        /// <returns>The captured certificate, or null if no certificate could be captured</returns>
        Task<X509Certificate2?> CaptureCertificateAsync(DnsEndPoint endpoint);
    }
}
