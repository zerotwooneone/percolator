using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Grpc.Net.Client;
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
        private readonly ITlsHandshakeService _tlsHandshakeService;
        
        // Keep strong references to active channels and certificates
        private readonly ConcurrentDictionary<string, GrpcChannel> _channels = 
            new ConcurrentDictionary<string, GrpcChannel>();
        private readonly ConcurrentDictionary<string, X509Certificate2> _certificates =
            new ConcurrentDictionary<string, X509Certificate2>();
        private readonly ConcurrentDictionary<string, HttpClient> _httpClients =
            new ConcurrentDictionary<string, HttpClient>();
            
        public GrpcSessionService(
            ILogger<GrpcSessionService> logger,
            IPeerTrustManager peerTrustManager,
            ITlsHandshakeService tlsHandshakeService)
        {
            _logger = logger;
            _peerTrustManager = peerTrustManager;
            _tlsHandshakeService = tlsHandshakeService;
        }

        public async Task<EstablishSessionResponse> EstablishSessionAsync(
            DnsEndPoint endpoint, EstablishSessionRequest request, X509Certificate2? remoteCert = null)
        {
            try
            {
                if (remoteCert == null)
                {
                    _logger.LogInformation("No certificate provided, performing TOFU handshake with {Endpoint}", endpoint);
                    
                    // Do a direct TLS handshake to capture the certificate first
                    remoteCert = await _tlsHandshakeService.CaptureCertificateAsync(endpoint);
                        
                    if (remoteCert == null)
                    {
                        throw new InvalidOperationException($"Failed to capture certificate from {endpoint}");
                    }
                    
                    _logger.LogInformation("TOFU: Captured certificate with thumbprint {Thumbprint} from {Endpoint}", 
                        remoteCert.Thumbprint, endpoint);
                }

                _logger.LogInformation("Establishing gRPC session with {Endpoint} using certificate with thumbprint {Thumbprint}", 
                    endpoint, remoteCert.Thumbprint);
                
                // Add to trust store for future connections
                await _peerTrustManager.AddTrustedPeer(remoteCert);
                
                // Store certificate in our dictionary to keep it alive throughout the connection
                string connectionKey = $"{endpoint}:{remoteCert.Thumbprint}";
                
                // First make a clean copy to avoid disposal issues
                var certData = remoteCert.GetRawCertData();
                var certCopy = X509CertificateLoader.LoadCertificate(certData);
                _certificates[connectionKey] = certCopy;

                // Clean up any existing resources for this endpoint
                await CleanupConnectionResourcesAsync(connectionKey);

                // Create HTTP handler with custom certificate validation
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                    {
                        if (cert == null)
                        {
                            _logger.LogWarning("No server certificate provided during validation");
                            return false;
                        }
                        
                        bool isMatch = cert.Thumbprint.Equals(certCopy.Thumbprint, StringComparison.OrdinalIgnoreCase);
                        
                        if (isMatch)
                        {
                            _logger.LogInformation("Certificate validation succeeded for {Endpoint}", endpoint);
                            return true;
                        }
                        else
                        {
                            _logger.LogWarning("Certificate mismatch! Expected {ExpectedThumbprint} but got {ActualThumbprint}",
                                certCopy.Thumbprint, cert.Thumbprint);
                            return false;
                        }
                    }
                };

                // Create and store HTTP client
                var httpClient = new HttpClient(handler);
                _httpClients[connectionKey] = httpClient;

                _logger.LogInformation("Creating gRPC channel to {Endpoint}", endpoint);

                // Create gRPC channel with the correct URI format
                var uri = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
                var channelOptions = new GrpcChannelOptions
                {
                    HttpClient = httpClient,
                    MaxReceiveMessageSize = 4 * 1024 * 1024,  // 4 MB
                    MaxSendMessageSize = 4 * 1024 * 1024      // 4 MB
                };

                var channel = GrpcChannel.ForAddress(uri, channelOptions);
                _channels[connectionKey] = channel;

                // Create client and make the call
                var client = new TransportService.TransportServiceClient(channel);

                _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);
                
                // Add a cancellation deadline
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));

                var response = await client.EstablishSessionAsync(request, cancellationToken: cts.Token);

                _logger.LogInformation("Session successfully established with {Endpoint}", endpoint);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish gRPC session with {Endpoint}: {ErrorMessage}", 
                    endpoint, ex.Message);
                
                // Clean up on failure
                string connectionKey = $"{endpoint}:{remoteCert?.Thumbprint ?? "unknown"}";
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
