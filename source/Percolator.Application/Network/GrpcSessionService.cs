using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Service for establishing gRPC sessions with remote peers
    /// </summary>
    public class GrpcSessionService : IGrpcSessionService
    {
        private readonly ILogger<GrpcSessionService> _logger;
        private readonly IPeerTrustManager _peerTrustManager;
        private readonly SharedCertificateManager _certificateManager;
        
        // Keep strong references to active resources
        private readonly ConcurrentDictionary<string, GrpcChannel> _channels = 
            new ConcurrentDictionary<string, GrpcChannel>();
        private readonly ConcurrentDictionary<string, X509Certificate2> _certificates =
            new ConcurrentDictionary<string, X509Certificate2>();
        private readonly ConcurrentDictionary<string, HttpClient> _httpClients =
            new ConcurrentDictionary<string, HttpClient>();
            
        public GrpcSessionService(
            ILogger<GrpcSessionService> logger,
            IPeerTrustManager peerTrustManager,
            SharedCertificateManager certificateManager)
        {
            _logger = logger;
            _peerTrustManager = peerTrustManager;
            _certificateManager = certificateManager;
        }

        public async Task<EstablishSessionResponse> EstablishSessionAsync(
            DnsEndPoint endpoint, EstablishSessionRequest request, X509Certificate2? remoteCert = null)
        {
            try
            {
                string connectionKey = $"{endpoint.Host}:{endpoint.Port}";
                _logger.LogInformation("Establishing gRPC session with {Endpoint}", endpoint);

                // Clean up any existing resources for this endpoint
                await CleanupConnectionResourcesAsync(connectionKey);

                // Get our shared certificate - this is the same certificate used by all peers
                var sharedCertificate = _certificateManager.GetServerCertificate(); 
                
                _logger.LogInformation("Using client certificate - Thumbprint: {Thumbprint}, Subject: {Subject}, HasPrivateKey: {HasPrivateKey}",
                    sharedCertificate.Thumbprint, sharedCertificate.Subject, sharedCertificate.HasPrivateKey);

                // Store certificate reference
                _certificates[connectionKey] = sharedCertificate;

                // Configure a SocketsHttpHandler which gives more control over TLS settings than HttpClientHandler
                var handler = new SocketsHttpHandler
                {
                    // Enable HTTP/2 explicitly - this also handles ALPN negotiation properly
                    EnableMultipleHttp2Connections = true,
                    
                    // Connection pooling and lifecycle management
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    MaxConnectionsPerServer = 10,
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    
                    // Configure TLS options
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        // Use our client certificate
                        ClientCertificates = new X509CertificateCollection { sharedCertificate },
                        
                        // Configure protocols
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        
                        // Explicitly enable protocols for ALPN
                        ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 },
                        
                        // TLS specific timeouts
                        TargetHost = endpoint.Host,
                        AllowRenegotiation = false,
                        
                        // Don't check revocation - this is a self-signed certificate
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        
                        // Configure server certificate validation
                        RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                        {
                            // Store the cert for future reference
                            var remoteCert = cert as X509Certificate2;
                            _certificates[connectionKey] = remoteCert;
                            
                            if (cert == null)
                            {
                                _logger.LogWarning("Remote server did not present a certificate");
                                return false;
                            }
                            
                            // Simply compare with our shared certificate thumbprint
                            bool isMatch = ((X509Certificate2)cert).Thumbprint.Equals(sharedCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
                            
                            if (isMatch)
                            {
                                _logger.LogInformation("Certificate validation succeeded for {Endpoint} with thumbprint {Thumbprint}", 
                                    endpoint, ((X509Certificate2)cert).Thumbprint);
                                return true;
                            }
                            else
                            {
                                _logger.LogWarning("Certificate mismatch! Expected {ExpectedThumbprint} but got {ActualThumbprint}",
                                    sharedCertificate.Thumbprint, ((X509Certificate2)cert).Thumbprint);
                                return false;
                            }
                        }
                    }
                };

                _logger.LogInformation("Created HTTP handler with HTTP/2 and TLS configuration");

                // Create and store HTTP client with longer timeout for debugging
                var httpClient = new HttpClient(handler);
                httpClient.Timeout = TimeSpan.FromSeconds(30); // Longer timeout for debugging
                _httpClients[connectionKey] = httpClient;

                _logger.LogInformation("Creating gRPC channel to {Endpoint}", endpoint);

                // Create gRPC channel with the correct URI format and explicit HTTP/2 configuration
                var uri = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
                var channelOptions = new GrpcChannelOptions
                {
                    HttpClient = httpClient,
                    MaxReceiveMessageSize = 4 * 1024 * 1024,  // 4 MB
                    MaxSendMessageSize = 4 * 1024 * 1024,     // 4 MB
                    DisposeHttpClient = false                 // We manage the HttpClient ourselves
                };

                _logger.LogInformation("Configured gRPC channel options with HTTP/2 support");

                var channel = GrpcChannel.ForAddress(uri, channelOptions);
                _channels[connectionKey] = channel;

                // Create client and make the call
                var client = new TransportService.TransportServiceClient(channel);

                _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);
                
                // Add a cancellation deadline
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));

                var response = await client.EstablishSessionAsync(request, cancellationToken: cts.Token);

                _logger.LogInformation("Session successfully established with {Endpoint}", endpoint);
                
                // If this is a new peer that we've never seen before, add it to the trusted peers
                if (remoteCert == null)
                {
                    await _peerTrustManager.AddTrustedPeer(sharedCertificate);
                }
                
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish gRPC session with {Endpoint}: {ErrorMessage}", 
                    endpoint, ex.Message);
                
                // Clean up on failure
                string connectionKey = $"{endpoint.Host}:{endpoint.Port}";
                await CleanupConnectionResourcesAsync(connectionKey);
                throw;
            }
        }

        private async Task CleanupConnectionResourcesAsync(string connectionKey)
        {
            // Clean up channel
            if (_channels.TryRemove(connectionKey, out var channel))
            {
                try 
                {
                    await channel.ShutdownAsync();
                    _logger.LogInformation("Cleaned up existing channel for {ConnectionKey}", connectionKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error shutting down channel for {ConnectionKey}: {ErrorMessage}", 
                        connectionKey, ex.Message);
                }
            }
            
            // Clean up HTTP client
            if (_httpClients.TryRemove(connectionKey, out var httpClient))
            {
                try
                {
                    httpClient.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing HTTP client for {ConnectionKey}: {ErrorMessage}", 
                        connectionKey, ex.Message);
                }
            }
            
            // Remove certificate
            _certificates.TryRemove(connectionKey, out _);
        }
    }
}
