using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Grpc.Core;
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
        private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
        private readonly ConcurrentDictionary<string, X509Certificate2> _certificates = new();
        private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new();
            
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
            DnsEndPoint endpoint, 
            EstablishSessionRequest request,
            X509Certificate2? remoteCert = null)
        {
            string connectionKey = $"{endpoint.Host}:{endpoint.Port}";
            
            try
            {
                _logger.LogInformation("Establishing session with {Endpoint}", endpoint);

                // Clean up any existing resources if they exist
                await CleanupConnectionResourcesAsync(connectionKey);
                
                // Get the shared certificate
                _logger.LogInformation("Getting shared certificate for mutual TLS");
                var sharedCertificate = _certificateManager.GetServerCertificate(); // Use server certificate with private key
                
                if (sharedCertificate == null)
                {
                    _logger.LogError("Failed to get shared certificate");
                    throw new InvalidOperationException("Failed to get shared certificate");
                }
                
                _logger.LogInformation("Using certificate: Subject={Subject}, Thumbprint={Thumbprint}, HasPrivateKey={HasPrivateKey}, NotBefore={NotBefore}, NotAfter={NotAfter}",
                    sharedCertificate.Subject,
                    sharedCertificate.Thumbprint,
                    sharedCertificate.HasPrivateKey,
                    sharedCertificate.NotBefore,
                    sharedCertificate.NotAfter);

                // Store the certificate for future reference
                _certificates[connectionKey] = sharedCertificate;

                // Create handler with proper HTTP/2 and TLS configuration
                var handler = new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        ClientCertificates = new X509CertificateCollection { sharedCertificate },
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        TargetHost = endpoint.Host
                    },
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    EnableMultipleHttp2Connections = true,
                    ConnectTimeout = TimeSpan.FromSeconds(10)
                };

                // Explicitly set HTTP/2 ALPN protocols
                var protocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http2
                };
                handler.SslOptions.ApplicationProtocols = protocols;
                
                _logger.LogInformation("Configured TLS with protocols: TLS1.2, TLS1.3 and ALPN for HTTP/2 only");

                // Certificate validation callback
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    // Store the cert for future reference
                    var remoteCert = cert as X509Certificate2;
                    _certificates[connectionKey] = remoteCert;
                    
                    if (cert == null)
                    {
                        _logger.LogWarning("Remote server did not present a certificate");
                        return false;
                    }
                    
                    _logger.LogInformation("Validating server certificate: Subject={Subject}, Thumbprint={Thumbprint}, Error={SslPolicyErrors}",
                        cert.Subject,
                        ((X509Certificate2)cert).Thumbprint,
                        errors);
                    
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
                };

                _logger.LogInformation("Created HTTP handler with HTTP/2 and TLS configuration");

                // Create and store HTTP client with longer timeout for debugging
                var httpClient = new HttpClient(handler);
                httpClient.Timeout = TimeSpan.FromSeconds(30); // Longer timeout for debugging
                _httpClients[connectionKey] = httpClient;

                _logger.LogInformation("Creating gRPC channel to {Endpoint}", endpoint);

                // Create gRPC channel with the correct URI format and explicit HTTP/2 configuration
                // For TLS connections, we need to use the correct TLS port based on how the server is configured
                // The server's MessageListenerService uses the provided port directly for TLS
                var uri = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
                var channelOptions = new GrpcChannelOptions
                {
                    HttpClient = httpClient,
                    MaxReceiveMessageSize = 4 * 1024 * 1024,  // 4 MB
                    MaxSendMessageSize = 4 * 1024 * 1024,     // 4 MB
                    DisposeHttpClient = false                 // We manage the HttpClient ourselves
                };

                _logger.LogInformation("Configured gRPC channel options with HTTP/2 support for TLS");

                var channel = GrpcChannel.ForAddress(uri, channelOptions);
                _channels[connectionKey] = channel;

                // Create client and make the call
                var client = new TransportService.TransportServiceClient(channel);

                _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);
                
                // Add a cancellation deadline
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));

                try {
                    var response = await client.EstablishSessionAsync(request, cancellationToken: cts.Token);
                    _logger.LogInformation("Session successfully established with {Endpoint}", endpoint);
                    return response;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to establish gRPC session with {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                    
                    // Enhanced error information
                    if (ex is RpcException rpcEx)
                    {
                        _logger.LogError("gRPC error details - Status: {Status}, Detail: {Detail}", 
                            rpcEx.Status.StatusCode, 
                            rpcEx.Status.Detail);
                    }
                    
                    // Check for inner HttpRequestException
                    if (ex.InnerException is HttpRequestException httpEx)
                    {
                        _logger.LogError("HTTP error details: {Message}", httpEx.Message);
                        
                        // Check for authentication exceptions
                        if (httpEx.InnerException is System.Security.Authentication.AuthenticationException authEx)
                        {
                            _logger.LogError("TLS authentication failed: {Message}", authEx.Message);
                            
                            // Check for more specific TLS handshake errors
                            if (authEx.InnerException != null)
                            {
                                _logger.LogError("TLS inner exception: {Type}: {Message}", 
                                    authEx.InnerException.GetType().Name,
                                    authEx.InnerException.Message);
                            }
                        }
                    }
                    
                    throw;
                }
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to establish gRPC session with {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message), "blah");
            }
            finally
            {
                // Don't clean up here as we want to keep the connection open
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
