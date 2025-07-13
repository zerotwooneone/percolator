using System;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Service for handling TLS handshakes and certificate capture
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
            
            try
            {
                // Capture the certificate using a fresh TCP connection and SSL stream
                X509Certificate2? remoteCert = await PerformTlsHandshakeAndCaptureAsync(endpoint);
                
                if (remoteCert != null)
                {
                    _logger.LogInformation("Successfully captured certificate with thumbprint {Thumbprint}", remoteCert.Thumbprint);
                }
                else
                {
                    _logger.LogWarning("No certificate captured from {Endpoint}", endpoint);
                }
                
                return remoteCert;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing certificate from {Endpoint}: {Message}", endpoint, ex.Message);
                return null;
            }
        }
        
        /// <summary>
        /// Performs a TLS handshake and captures the remote certificate
        /// </summary>
        /// <param name="endpoint">The endpoint to connect to</param>
        /// <returns>A cloned certificate that is independent of the SslStream</returns>
        private async Task<X509Certificate2?> PerformTlsHandshakeAndCaptureAsync(DnsEndPoint endpoint)
        {
            X509Certificate2? remoteCert = null;
            byte[]? certData = null;
            
            // Use a completely separate TCP connection just for certificate capture
            using var tcpClient = new System.Net.Sockets.TcpClient();
            
            try
            {
                await tcpClient.ConnectAsync(endpoint.Host, endpoint.Port);
                _logger.LogDebug("TCP connection established with {Endpoint}", endpoint);
                
                using var sslStream = new SslStream(
                    tcpClient.GetStream(),
                    false,
                    (sender, certificate, chain, errors) => 
                    {
                        if (certificate != null)
                        {
                            try
                            {
                                // Store raw certificate data
                                certData = certificate.Export(X509ContentType.Cert);
                                _logger.LogDebug("Certificate data captured during validation: {Length} bytes", certData.Length);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error exporting certificate data: {Message}", ex.Message);
                            }
                        }
                        
                        // Always return true during certificate capture
                        return true;
                    });
                
                // Important: Avoid setting RemoteCertificateValidationCallback twice
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
                
                _logger.LogDebug("TLS handshake completed successfully");
                
                // If we didn't get the certificate data from the callback, try to get it directly
                if (certData == null && sslStream.RemoteCertificate != null)
                {
                    try 
                    {
                        certData = sslStream.RemoteCertificate.Export(X509ContentType.Cert);
                        _logger.LogDebug("Certificate data captured after handshake: {Length} bytes", certData.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error exporting certificate after handshake: {Message}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TLS handshake failed: {Message}", ex.Message);
                return null;
            }
            
            // Process the certificate data outside the TCP/SslStream scope to avoid any disposal issues
            if (certData != null)
            {
                try
                {
                    // Create a completely independent certificate from the raw data
                    // Using the static method in .NET 9.0
                    remoteCert = X509CertificateLoader.LoadCertificate(certData);
                    
                    _logger.LogDebug("Successfully created independent certificate with thumbprint {Thumbprint}", 
                        remoteCert.Thumbprint);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create certificate from data: {Message}", ex.Message);
                }
            }
            
            return remoteCert;
        }
    }
}
