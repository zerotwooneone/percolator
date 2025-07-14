using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.Network;

/// <summary>
/// Service for handling TLS handshakes and certificate capture
/// </summary>
public class TlsHandshakeService : ITlsHandshakeService
{
    private readonly ILogger<TlsHandshakeService> _logger;
    private readonly SharedCertificateManager _certificateManager;

    public TlsHandshakeService(
        ILogger<TlsHandshakeService> logger,
        SharedCertificateManager certificateManager)
    {
        _logger = logger;
        _certificateManager = certificateManager;
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
    /// Performs a direct TLS handshake with the endpoint and captures the server certificate
    /// </summary>
    private async Task<X509Certificate2?> PerformTlsHandshakeAndCaptureAsync(DnsEndPoint endpoint)
    {
        X509Certificate2? serverCertificate = null;
        
        // Get our client certificate for mutual TLS
        X509Certificate2 clientCertificate = _certificateManager.GetServerCertificate();
        
        _logger.LogInformation("Using client certificate with thumbprint {Thumbprint} for handshake", 
            clientCertificate.Thumbprint);

        // Create TCP client for the raw connection
        using var tcpClient = new TcpClient();
        
        try
        {
            _logger.LogInformation("Connecting to {Endpoint}...", endpoint);
            
            // Connect to the endpoint with a timeout
            var connectTask = tcpClient.ConnectAsync(endpoint.Host, endpoint.Port);
            if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
            {
                throw new TimeoutException($"Connection to {endpoint} timed out after 5 seconds");
            }
            
            await connectTask; // Ensure any exceptions are propagated
            
            _logger.LogInformation("TCP connection established to {Endpoint}", endpoint);
            
            // Create SSL stream with our client certificate
            using var sslStream = new SslStream(
                tcpClient.GetStream(),
                false,
                // Accept the server certificate for inspection - we're just capturing it here
                (sender, certificate, chain, errors) => 
                {
                    if (certificate == null)
                    {
                        _logger.LogWarning("Remote server did not present a certificate");
                        return false;
                    }
                    
                    _logger.LogInformation("Received server certificate with thumbprint {Thumbprint}", 
                        certificate.GetCertHashString());
                    
                    // Capture the server certificate
                    serverCertificate = new X509Certificate2(certificate);
                    
                    // In shared certificate model, validate against our certificate
                    bool isMatch = serverCertificate.Thumbprint.Equals(
                        clientCertificate.Thumbprint, 
                        StringComparison.OrdinalIgnoreCase);
                        
                    if (!isMatch)
                    {
                        _logger.LogWarning("Server certificate validation failed. Expected: {Expected}, Got: {Actual}",
                            clientCertificate.Thumbprint, serverCertificate.Thumbprint);
                    }
                    
                    // Return true to continue the handshake and get the certificate
                    // even if it doesn't match - we're just capturing it at this point
                    return true;
                });

            _logger.LogInformation("Starting TLS handshake as client...");
            
            // Prepare client certificates collection
            var clientCertificates = new X509CertificateCollection();
            clientCertificates.Add(clientCertificate);
            
            // Initiate the TLS handshake with explicit protocol versions
            // Must match what's configured on the server
            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Host,
                    ClientCertificates = clientCertificates,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                });
            
            _logger.LogInformation("TLS handshake successful with {Endpoint}, Protocol: {Protocol}", 
                endpoint, sslStream.SslProtocol.ToString());
            
            return serverCertificate;
        }
        catch (AuthenticationException authEx)
        {
            _logger.LogError(authEx, "TLS authentication failed: {Message}", authEx.Message);
            
            // We can still return the captured certificate even if authentication failed
            return serverCertificate;
        }
        catch (IOException ioEx)
        {
            _logger.LogError(ioEx, "IO error during TLS handshake: {Message}", ioEx.Message);
            return serverCertificate;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during TLS handshake: {Message}", ex.Message);
            return serverCertificate;
        }
    }
}
