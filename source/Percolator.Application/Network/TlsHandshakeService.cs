using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Default implementation of ITlsHandshakeService
    /// </summary>
    public class TlsHandshakeService : ITlsHandshakeService
    {
        private readonly ILogger<TlsHandshakeService> _logger;

        public TlsHandshakeService(ILogger<TlsHandshakeService> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<X509Certificate2?> CaptureCertificateAsync(DnsEndPoint endpoint)
        {
            _logger.LogInformation("Attempting to capture remote certificate through direct TLS handshake with {Endpoint}", endpoint);
            X509Certificate2? remoteCert = null;
            
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                await tcpClient.ConnectAsync(endpoint.Host, endpoint.Port);
                
                using var sslStream = new SslStream(
                    tcpClient.GetStream(),
                    false,
                    (sender, certificate, chain, errors) => 
                    {
                        // Capture the certificate but always return true during TOFU
                        if (certificate != null)
                        {
                            // Create a completely independent copy of the certificate by exporting and reimporting it
                            var tempCert = new X509Certificate2(certificate);
                            byte[] certBytes = tempCert.Export(X509ContentType.Cert);
                            remoteCert = new X509Certificate2(certBytes);
                            
                            _logger.LogInformation("Captured certificate with thumbprint {Thumbprint} during TLS handshake", 
                                remoteCert.Thumbprint);
                        }
                        return true;
                    });
                
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = endpoint.Host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        // Removed duplicate RemoteCertificateValidationCallback that was causing the error
                    });
                
                // If we got here, the handshake succeeded
                _logger.LogInformation("TLS Handshake successful, certificate captured: {HasCert}", remoteCert != null);
                return remoteCert;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error during certificate capture: {Message}", ex.Message);
                return remoteCert;  // Return the cert if we captured it before the error
            }
        }
    }
}
