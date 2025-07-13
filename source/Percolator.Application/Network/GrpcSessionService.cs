using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;
using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Default implementation of IGrpcSessionService for establishing gRPC sessions with TOFU support
    /// </summary>
    public class GrpcSessionService : IGrpcSessionService
    {
        private readonly ILogger<GrpcSessionService> _logger;
        private readonly IPeerTrustManager _peerTrustManager;
        private readonly IHttpClientFactory _httpClientFactory;

        public GrpcSessionService(
            ILogger<GrpcSessionService> logger,
            IPeerTrustManager peerTrustManager,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _peerTrustManager = peerTrustManager;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public async Task<EstablishSessionResponse> EstablishSessionAsync(
            DnsEndPoint endpoint, 
            EstablishSessionRequest request, 
            X509Certificate2? remoteCert = null)
        {
            if (remoteCert != null)
            {
                return await EstablishSessionWithTrustedCertAsync(endpoint, request, remoteCert);
            }
            
            try
            {
                _logger.LogInformation("Establishing gRPC session with {Endpoint}", endpoint);
                
                // Use the default HTTP client if no certificate is provided
                var httpClient = _httpClientFactory.CreateClient("percolator-grpc");
                var channelOptions = new GrpcChannelOptions 
                { 
                    HttpClient = httpClient,
                    DisposeHttpClient = false,
                    ThrowOperationCanceledOnCancellation = true,
                    MaxReceiveMessageSize = null,
                    MaxSendMessageSize = null
                };
                
                using var channel = GrpcChannel.ForAddress($"https://{endpoint.Host}:{endpoint.Port}", channelOptions);
                var client = new TransportService.TransportServiceClient(channel);
                var callOptions = new CallOptions(
                    deadline: DateTime.UtcNow.AddSeconds(45),
                    headers: new Metadata { { "grpc-timeout", "45S" } }
                );
                
                return await client.EstablishSessionAsync(request, callOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish gRPC session with {Endpoint}", endpoint);
                throw;
            }
        }
        
        private async Task<EstablishSessionResponse> EstablishSessionWithTrustedCertAsync(
            DnsEndPoint endpoint, 
            EstablishSessionRequest request,
            X509Certificate2 remoteCert)
        {
            // Add the certificate to the trusted certs for this request
            await _peerTrustManager.AddTrustedPeer(remoteCert);
            
            try 
            {
                _logger.LogInformation("TOFU: Creating retry channel with trusted certificate for {Endpoint}", endpoint);
                
                // Create a handler that accepts the specific certificate
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                    EnableMultipleHttp2Connections = true,
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                        {
                            if (certificate == null)
                            {
                                _logger.LogWarning("TOFU: No certificate provided during validation callback");
                                return false;
                            }

                            // For TOFU, we explicitly trust this specific certificate
                            using var cert2 = new X509Certificate2(certificate);
                            var isMatch = cert2.Thumbprint == remoteCert.Thumbprint;
                            
                            if (!isMatch)
                            {
                                _logger.LogWarning("TOFU: Certificate thumbprint mismatch. Expected: {Expected}, Got: {Actual}", 
                                    remoteCert.Thumbprint, cert2.Thumbprint);
                            }
                            else
                            {
                                _logger.LogInformation("TOFU: Certificate thumbprint match confirmed: {Thumbprint}", cert2.Thumbprint);
                            }
                            
                            return isMatch;
                        }
                    }
                };

                // Create HTTP client with our custom handler
                var httpClient = new HttpClient(handler)
                {
                    BaseAddress = new Uri($"https://{endpoint.Host}:{endpoint.Port}"),
                    Timeout = TimeSpan.FromSeconds(30)
                };
                
                var retryChannelOptions = new GrpcChannelOptions 
                {
                    HttpClient = httpClient,
                    DisposeHttpClient = true,
                    ThrowOperationCanceledOnCancellation = true,
                    MaxReceiveMessageSize = null,
                    MaxSendMessageSize = null
                };
                
                using var retryChannel = GrpcChannel.ForAddress($"https://{endpoint.Host}:{endpoint.Port}", retryChannelOptions);
                var retryClient = new TransportService.TransportServiceClient(retryChannel);

                _logger.LogInformation("TOFU: Sending retry EstablishSession request to {Endpoint}", endpoint);
                // Use a longer timeout for the retry connection
                var retryCallOptions = new CallOptions(deadline: DateTime.UtcNow.AddSeconds(30));
                return await retryClient.EstablishSessionAsync(request, retryCallOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TOFU: Failed to establish retry connection with peer at {Endpoint}", endpoint);
                throw;
            }
        }
    }
}
