using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;

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
        
        public async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
            DnsEndPoint endpoint, 
            EstablishDirectSessionRequest request)
        {
            return await Inner_EstablishSession(endpoint, request).ConfigureAwait(false);
        }

        public async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(
            DnsEndPoint endpoint,
            InviteHandshakeResponse request)
        {
            string connectionKey = $"{endpoint.Host}:{endpoint.Port}";

            try
            {
                _logger.LogInformation("Delivering InviteHandshakeResponse to {Endpoint}", endpoint);

                // Clean up any existing resources if they exist
                await CleanupConnectionResourcesAsync(connectionKey).ConfigureAwait(false);

                // Determine if we're connecting to localhost
                bool isLocalConnection = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                         IPAddress.TryParse(endpoint.Host, out var ip) &&
                                         (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any));

                _logger.LogInformation("Connection to {Endpoint} identified as {ConnectionType}",
                    endpoint, isLocalConnection ? "local" : "remote");

                SocketsHttpHandler handler;
                Uri uri;

                if (isLocalConnection)
                {
                    handler = new SocketsHttpHandler
                    {
                        EnableMultipleHttp2Connections = true,
                        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                        KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                        ConnectTimeout = TimeSpan.FromSeconds(10)
                    };

                    uri = new Uri($"http://{endpoint.Host}:{endpoint.Port}");
                }
                else
                {
                    var sharedCertificate = _certificateManager.GetServerCertificate();

                    if (sharedCertificate == null)
                    {
                        _logger.LogError("Failed to get shared certificate");
                        throw new InvalidOperationException("Failed to get shared certificate");
                    }

                    _certificates[connectionKey] = sharedCertificate;

                    handler = new SocketsHttpHandler
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

                    handler.SslOptions.ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        SslApplicationProtocol.Http2
                    };

                    handler.SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        var remoteCert = cert as X509Certificate2;
                        _certificates[connectionKey] = remoteCert;

                        if (cert == null)
                        {
                            _logger.LogWarning("Remote server did not present a certificate");
                            return false;
                        }

                        _logger.LogWarning("*** ACCEPTING ANY CERTIFICATE FOR TESTING - INSECURE ***");
                        return true;
                    };

                    uri = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
                }

                var httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(300)
                };
                _httpClients[connectionKey] = httpClient;

                var channelOptions = new GrpcChannelOptions
                {
                    HttpClient = httpClient,
                    MaxReceiveMessageSize = 4 * 1024 * 1024,
                    MaxSendMessageSize = 4 * 1024 * 1024,
                    DisposeHttpClient = false
                };

                var channel = GrpcChannel.ForAddress(uri, channelOptions);
                _channels[connectionKey] = channel;

                var client = new TransportService.TransportServiceClient(channel);

                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(300));
                return await client.DeliverInviteHandshakeResponseAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to deliver InviteHandshakeResponse to {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

        private async Task<EstablishDirectSessionResponse> Inner_EstablishSession(
            DnsEndPoint endpoint, 
            EstablishDirectSessionRequest request)
        {
            string connectionKey = $"{endpoint.Host}:{endpoint.Port}";

            try
            {
                _logger.LogInformation("Establishing session with {Endpoint}", endpoint);

                // Clean up any existing resources if they exist
                await CleanupConnectionResourcesAsync(connectionKey).ConfigureAwait(false);
                
                // Determine if we're connecting to localhost
                bool isLocalConnection = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || 
                                         IPAddress.TryParse(endpoint.Host, out var ip) && 
                                         (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any));
                
                _logger.LogInformation("Connection to {Endpoint} identified as {ConnectionType}", 
                    endpoint, isLocalConnection ? "local" : "remote");

                SocketsHttpHandler handler;
                Uri uri;

                if (isLocalConnection)
                {
                    // For local connections, use HTTP/2 without TLS
                    _logger.LogInformation("Using HTTP/2 without TLS for local connection");
                    
                    handler = new SocketsHttpHandler
                    {
                        EnableMultipleHttp2Connections = true,
                        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                        KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                        KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                        ConnectTimeout = TimeSpan.FromSeconds(10)
                    };
                    
                    uri = new Uri($"http://{endpoint.Host}:{endpoint.Port}");
                    _logger.LogInformation("Using HTTP URI for local connection: {Uri}", uri);
                }
                else
                {
                    // For remote connections, use TLS with client certificate
                    _logger.LogInformation("Getting shared certificate for mutual TLS (remote connection)");
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
                    handler = new SocketsHttpHandler
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
                        
                        // TEMPORARY FOR TESTING: Accept any certificate to get the connection working
                        _logger.LogWarning("*** ACCEPTING ANY CERTIFICATE FOR TESTING - INSECURE ***");
                        return true;
                    };
                    
                    uri = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
                    _logger.LogInformation("Using HTTPS URI for remote connection: {Uri}", uri);
                }

                _logger.LogInformation("Created HTTP handler with appropriate HTTP/2 configuration");

                // Create and store HTTP client with longer timeout for debugging
                var httpClient = new HttpClient(handler);
                //todo: make timeout configurable
                httpClient.Timeout = TimeSpan.FromSeconds(300); // Longer timeout for debugging
                _httpClients[connectionKey] = httpClient;

                _logger.LogInformation("Creating gRPC channel to {Endpoint}", endpoint);

                // Create gRPC channel with the correct URI format and explicit HTTP/2 configuration
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

                // Test if the channel is available
                try
                {
                    _logger.LogInformation("Testing channel connectivity...");
                    var connectivityState = channel.State;
                    _logger.LogInformation("Initial channel state: {State}", connectivityState);
                    
                    // This will force a connection attempt
                    await channel.ConnectAsync().ConfigureAwait(false);
                    
                    _logger.LogInformation("Successfully connected to gRPC server at {Uri}", uri);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Initial channel connectivity test failed: {Error}", ex.Message);
                    _logger.LogInformation("Continuing anyway as this might be expected during initial connection...");
                    // We'll continue despite this error since the actual RPC call might still work
                }

                // Create client and make the call
                var client = new TransportService.TransportServiceClient(channel);

                _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);
                
                // todo: make cancellation deadline configurable
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(300));

                try {
                    var response = await client.EstablishDirectSessionAsync(request, cancellationToken: cts.Token);
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
                    await channel.ShutdownAsync().ConfigureAwait(false);
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
