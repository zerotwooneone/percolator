using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Percolator.Node
{
    public static class TlsDebugger
    {
        /// <summary>
        /// Tests a basic TLS handshake with a server to diagnose TLS connectivity issues.
        /// </summary>
        public static async Task TestTlsHandshake(DnsEndPoint endpoint, ILogger logger)
        {
            logger.LogInformation("Starting TLS handshake test with {Endpoint}", endpoint);
            
            try
            {
                // Create TCP client and connect to endpoint
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(endpoint.Host, endpoint.Port);
                logger.LogInformation("TCP connection established to {Endpoint}", endpoint);
                
                // Create SSL stream for the TLS handshake
                using var sslStream = new SslStream(
                    tcpClient.GetStream(),
                    false,
                    (sender, certificate, chain, errors) => 
                    {
                        // Log certificate details and validation errors
                        logger.LogInformation("Server certificate: Subject={Subject}, Issuer={Issuer}, Errors={Errors}",
                            certificate?.Subject ?? "null",
                            certificate?.Issuer ?? "null",
                            errors);
                        
                        // Accept all certificates for testing
                        return true;
                    });
                
                // Attempt TLS handshake with server
                await sslStream.AuthenticateAsClientAsync(
                    endpoint.Host,
                    null, // No client certificates for this test
                    SslProtocols.Tls12 | SslProtocols.Tls13,
                    false); // Don't check certificate revocation
                
                logger.LogInformation("TLS handshake successful with {Endpoint}. Protocol: {Protocol}, Cipher: {Cipher}, Hash: {Hash}",
                    endpoint,
                    sslStream.SslProtocol,
                    sslStream.CipherAlgorithm,
                    sslStream.HashAlgorithm);
                    
                // If we get here, basic TLS is working
                logger.LogInformation("✓ TLS CONNECTIVITY TEST PASSED: Basic TLS handshake completed successfully");
            }
            catch (SocketException ex)
            {
                logger.LogError("✗ TCP CONNECTION FAILED: Cannot establish TCP connection to {Endpoint}. Error: {Message}", 
                    endpoint, ex.Message);
            }
            catch (AuthenticationException ex)
            {
                logger.LogError("✗ TLS HANDSHAKE FAILED: TLS authentication failed with {Endpoint}. Error: {Message}", 
                    endpoint, ex.Message);
            }
            catch (IOException ex)
            {
                logger.LogError("✗ TLS CONNECTION TERMINATED: The connection was terminated during handshake with {Endpoint}. Error: {Message}", 
                    endpoint, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "✗ UNEXPECTED ERROR: TLS test failed with an unexpected error");
            }
        }
        
        /// <summary>
        /// Performs a TLS handshake test against the endpoint with a client certificate
        /// </summary>
        public static async Task TestMutualTlsHandshake(DnsEndPoint endpoint, X509Certificate2 clientCertificate, ILogger logger)
        {
            logger.LogInformation("Starting mutual TLS handshake test with {Endpoint}", endpoint);
            
            try
            {
                // Create TCP client and connect to endpoint
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(endpoint.Host, endpoint.Port);
                
                // Create SSL stream for the mTLS handshake
                using var sslStream = new SslStream(
                    tcpClient.GetStream(),
                    false,
                    (sender, certificate, chain, errors) => 
                    {
                        // Log certificate details and validation errors
                        logger.LogInformation("Server certificate: Subject={Subject}, Issuer={Issuer}, Errors={Errors}",
                            certificate?.Subject ?? "null",
                            certificate?.Issuer ?? "null",
                            errors);
                        
                        // Accept all certificates for testing
                        return true;
                    });
                
                // Create certificate collection with our client certificate
                var clientCerts = new X509CertificateCollection { clientCertificate };
                
                // Attempt TLS handshake with server
                await sslStream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = endpoint.Host,
                        ClientCertificates = clientCerts,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
                    });
                
                logger.LogInformation("Mutual TLS handshake successful with {Endpoint}. Protocol: {Protocol}", 
                    endpoint, sslStream.SslProtocol);
                    
                // If we get here, mutual TLS is working
                logger.LogInformation("✓ MUTUAL TLS TEST PASSED: TLS handshake with client certificate completed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "✗ MUTUAL TLS TEST FAILED: {Message}", ex.Message);
            }
        }
    }
}
