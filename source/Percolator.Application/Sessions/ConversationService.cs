using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Sessions;
using System.Security.Cryptography;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using NetworkPeerId = Percolator.Network.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.Sessions;

public class ConversationService : IConversationService
{
    private readonly ILogger<ConversationService> _logger;
    private readonly IX3DHOrchestrator _orchestrator;
    private readonly IDirectSessionManager _sessionManager;
    private readonly IConversationRepository _conversationRepository;
    private readonly IPeerRepository _peerRepository;
    private readonly IPeerConnectionRepository _peerConnectionRepository;
    private readonly ITlsCertificateService _tlsCertificateService;
    private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPeerTrustManager _peerTrustManager;

    public ConversationService(
        ILogger<ConversationService> logger,
        IX3DHOrchestrator orchestrator,
        IDirectSessionManager sessionManager,
        IConversationRepository conversationRepository,
        IPeerRepository peerRepository,
        IPeerConnectionRepository peerConnectionRepository,
        ITlsCertificateService tlsCertificateService,
        IOneTimeKeyProvider oneTimeKeyProvider,
        ActiveIdentityContext activeIdentityContext,
        IHttpClientFactory httpClientFactory,
        IPeerTrustManager peerTrustManager)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _sessionManager = sessionManager;
        _conversationRepository = conversationRepository;
        _peerRepository = peerRepository;
        _peerConnectionRepository = peerConnectionRepository;
        _tlsCertificateService = tlsCertificateService;
        _oneTimeKeyProvider = oneTimeKeyProvider;
        _activeIdentityContext = activeIdentityContext;
        _httpClientFactory = httpClientFactory;
        _peerTrustManager = peerTrustManager;
    }

    public async Task<ChatConversationId> CreateDirectConversationAsync(
        DnsEndPoint endpoint, 
        string peerName)
    {
        return await CreateDirectConversationWithKeyAsync(endpoint, peerName, null);
    }

    private async Task<ChatConversationId> CreateDirectConversationWithKeyAsync(
        DnsEndPoint endpoint, 
        string peerName, 
        byte[]? remoteTlsKey = null)
    {
        // Make sure we have a local identity
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("No active identity to establish a conversation with.");
        }

        var localIdentity = _activeIdentityContext.Identity;
        var localKeys = _activeIdentityContext.Keys ?? throw new InvalidOperationException("Cannot create a conversation without active keys.");

        // 1. Generate the ephemeral key for this handshake
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var signedPreKeyPublicBytes = localKeys.SignedPreKey.ExportSubjectPublicKeyInfo();
        var signature = localKeys.IdentitySigningKey.SignData(signedPreKeyPublicBytes, HashAlgorithmName.SHA256);
        var oneTimeKey = _oneTimeKeyProvider.PopOneTimeKey();

        // 2. Build the correct request object
        var request = new EstablishSessionRequest
        {
            InitiatorBundle = new ContractsPreKeyBundle
            {
                IdentitySigningKey = ByteString.CopyFrom(localKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                IdentityAgreementKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo()),
                SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
                PreKeySignature = ByteString.CopyFrom(signature),
                OneTimePreKey = oneTimeKey is null ? ByteString.Empty : ByteString.CopyFrom(oneTimeKey.ExportSubjectPublicKeyInfo())
            },
            InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKey.ExportSubjectPublicKeyInfo())
        };

        if (remoteTlsKey != null)
        {
            try
            {
                _logger.LogInformation("Creating direct conversation with peer {PeerName} at {Endpoint} using provided TLS key", 
                    peerName, endpoint);
                var httpClient = _httpClientFactory.CreateClient("percolator-grpc");

                // Create the channel
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

                var callOptions = new CallOptions(deadline: DateTime.UtcNow.AddSeconds(30));
                var response = await client.EstablishSessionAsync(request, callOptions);

                // Create or retrieve the peer and peer connection
                var peer = await _peerRepository.GetByNameAsync(peerName) 
                    ?? await CreatePeerAsync(peerName, response, endpoint);
                
                // Complete the handshake with the received bundle
                var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

                var conversationId = new ChatConversationId(Guid.NewGuid());
                var conversation = new ChatConversation(
                    conversationId,
                    new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                    new List<ChatParticipantId> { new(localIdentity.Id), new(peer.Id.Value) },
                    new List<Message>(),
                    peerName);

                await _conversationRepository.AddAsync(conversation);

                await _sessionManager.EstablishSessionAsInitiatorAsync(
                    new SessionConversationId(conversation.Id.Value),
                    new SessionPeerId(peer.Id.Value),
                    new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
                    new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                    new SessionSharedSecret(sharedSecret.Value));

                return conversation.Id;
            }
            catch (RpcException ex)
            {
                _logger.LogError(ex, "Failed to establish session with peer {PeerName} at {Endpoint}: {ErrorMessage}", 
                    peerName, endpoint, ex.Message);
                throw;
            }
        }
        
        // We don't have a trusted remote TLS key, so we need to perform Trust On First Use (TOFU)
        try 
        {
            _logger.LogInformation("Attempting Trust-on-First-Use connection with {PeerName} at {Endpoint}", peerName, endpoint);

            // First try direct TLS handshake to capture the certificate
            var remoteCert = await CaptureRemoteCertificateAsync(endpoint);
            if (remoteCert != null)
            {
                _logger.LogInformation("Successfully captured certificate during initial handshake");
                var result = await HandleTofuWithCapturedCertificateAsync(endpoint, peerName, remoteCert, request, ephemeralKey);
                if (result != null)
                {
                    return result.Value;
                }
                throw new InvalidOperationException("TOFU handshake failed after capturing certificate");
            }
            
            // Fall back to regular TOFU handling if we couldn't capture the certificate
            var conversationId = await HandleRegularTofuAsync(endpoint, peerName, request, ephemeralKey);
            if (conversationId != null)
            {
                return conversationId.Value;
            }
            throw new InvalidOperationException("Failed to establish connection during TOFU process");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && 
                                      (ex.Status.Detail.Contains("The SSL connection could not be established") ||
                                       ex.Status.Detail.Contains("HttpRequestException")))
        {
            _logger.LogWarning(ex, "TLS handshake failed during TOFU with {PeerName} at {Endpoint}. This is expected during initial connection. Details: {ErrorDetails}", 
                peerName, endpoint, ex.Status.Detail);
                
            // The error might have the certificate info we need for TOFU
            try 
            {
                var conversationId = await HandleRegularTofuAsync(endpoint, peerName, request, ephemeralKey);
                if (conversationId != null)
                {
                    return conversationId.Value;
                }
                throw new InvalidOperationException("Failed to establish connection during TOFU retry");
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Failed to recover from TLS handshake error during TOFU");
                throw new InvalidOperationException("Failed to establish secure connection during Trust-on-First-Use", innerEx);
            }
        }
    }

    protected virtual async Task<ChatConversationId?> HandleRegularTofuAsync(
        DnsEndPoint endpoint, 
        string peerName, 
        EstablishSessionRequest request, 
        ECDiffieHellman ephemeralKey)
    {
        // Try to extract the peer's certificate from the TLS error
        try
        {
            _logger.LogInformation("TOFU: First attempt to establish session with {PeerName} at {Endpoint}", peerName, endpoint);
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
            
            // Use a longer timeout for the first attempt
            var callOptions = new CallOptions(
                deadline: DateTime.UtcNow.AddSeconds(45),
                headers: new Metadata { { "grpc-timeout", "45S" } }
            );
            
            var response = await client.EstablishSessionAsync(request, callOptions);
            
            _logger.LogWarning("TOFU: First connection attempt unexpectedly succeeded without certificate validation");
            
            // This should not normally happen during TOFU - the first connection should fail
            // But if it succeeds, we'll handle it gracefully by capturing the certificate manually
            
            // Perform a separate TLS handshake to explicitly capture the certificate
            var remoteCert = await CaptureRemoteCertificateAsync(endpoint);
            if (remoteCert != null)
            {
                _logger.LogInformation("TOFU: Successfully captured certificate after connection succeeded");
                return await HandleTofuWithCapturedCertificateAsync(
                    endpoint, peerName, remoteCert, request, ephemeralKey);
            }
            
            _logger.LogWarning("TOFU: Could not capture certificate for successful connection");
            return null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable || 
                                      ex.StatusCode == StatusCode.Internal ||
                                      ex.StatusCode == StatusCode.Unknown)
        {
            _logger.LogInformation("TOFU: Connection failed as expected during TOFU: {Message}", ex.Message);

            // Try to capture the certificate through a direct TLS handshake
            var remoteCert = await CaptureRemoteCertificateAsync(endpoint);
            if (remoteCert != null)
            {
                _logger.LogInformation("TOFU: Successfully captured certificate after RpcException");
                return await HandleTofuWithCapturedCertificateAsync(
                    endpoint, peerName, remoteCert, request, ephemeralKey);
            }
            
            _logger.LogWarning("TOFU: Could not capture certificate after RpcException: {Message}", ex.Message);
            throw;
        }
    }
    
    protected virtual async Task<X509Certificate2?> CaptureRemoteCertificateAsync(DnsEndPoint endpoint)
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
                        remoteCert = new X509Certificate2(certificate);
                        _logger.LogInformation("Captured certificate with thumbprint {Thumbprint} during TLS handshake", 
                            remoteCert.Thumbprint);
                    }
                    return true;
                });
            
            await sslStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => 
                    {
                        // Same callback to ensure capture
                        if (certificate != null && remoteCert == null)
                        {
                            remoteCert = new X509Certificate2(certificate);
                            _logger.LogInformation("Captured certificate in second callback with thumbprint {Thumbprint}", 
                                remoteCert.Thumbprint);
                        }
                        return true;
                    }
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
    
    protected virtual async Task<ChatConversationId?> HandleTofuWithCapturedCertificateAsync(
        DnsEndPoint endpoint,
        string peerName,
        X509Certificate2 remoteCert,
        EstablishSessionRequest request,
        ECDiffieHellman ephemeralKey)
    {
        var tlsCertificate = new TlsCertificate(remoteCert.Export(X509ContentType.Cert));

        var existingConnection = await _peerConnectionRepository.GetByTlsCertificateAsync(tlsCertificate);
        if (existingConnection is not null)
        {
            _logger.LogWarning("Found existing peer connection with this certificate, but it's not trusted. Adding to trust store.");
            
            // Even though the connection exists, the certificate is not trusted - add it to the trust store
            await _peerTrustManager.AddTrustedPeer(remoteCert);
            
            // Get the peer ID for the existing connection
            var networkPeerId = existingConnection.Id;
            var peer = await _peerRepository.GetByIdAsync(new Percolator.Identity.PeerId(networkPeerId.Value));
            if (peer == null)
            {
                _logger.LogError("Peer not found for existing connection with ID {PeerId}", networkPeerId);
                throw new InvalidOperationException("Peer connection exists but peer not found.");
            }
            
            // Continue with the retry using the existing peer data
            return await RetryConnectionWithTrustedCertAsync(endpoint, peerName, remoteCert, request, ephemeralKey, peer.Id, existingConnection);
        }

        // 1. Create and save the new Peer and PeerConnection
        var identityPeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var newPeer = new Percolator.Identity.Peer(identityPeerId, peerName);
        await _peerRepository.AddAsync(newPeer);

        var newConnection = new PeerConnection(
            new NetworkPeerId(newPeer.Id.Value),
            null, // DirectMessagePublicKey is not yet known
            new[] { new GrpcEndPoint(new DnsEndPoint(endpoint.Host, endpoint.Port), DateTimeOffset.UtcNow) },
            new[] { tlsCertificate },
            DateTimeOffset.UtcNow);
        await _peerConnectionRepository.SaveAsync(newConnection);

        // 2. Add the certificate to the trust store so the next attempt succeeds
        await _peerTrustManager.AddTrustedPeer(remoteCert);

        // 3. Retry the connection with the newly created peer
        return await RetryConnectionWithTrustedCertAsync(endpoint, peerName, remoteCert, request, ephemeralKey, identityPeerId, newConnection);
    }
    
    private async Task<ChatConversationId?> RetryConnectionWithTrustedCertAsync(
        DnsEndPoint endpoint, 
        string peerName, 
        X509Certificate2 remoteCert,
        EstablishSessionRequest request,
        ECDiffieHellman ephemeralKey,
        Percolator.Identity.PeerId peerId,
        PeerConnection? existingConnection = null)
    {
        // Add the certificate to the trusted certs for this request
        await _peerTrustManager.AddTrustedPeer(remoteCert);
        
        try 
        {
            var response = await PerformRetryConnectionAsync(endpoint, remoteCert, request);
            
            _logger.LogInformation("TOFU: Successfully received retry EstablishSession response from {Endpoint}", endpoint);

            // Persist the DirectMessagePublicKey now that we have it
            var directMessagePublicKey = new DirectMessagePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray());
            
            // Update the existing connection if we have one, otherwise we just created a new one
            if (existingConnection != null)
            {
                await _peerConnectionRepository.UpdateDirectMessagePublicKeyAsync(existingConnection.Id, directMessagePublicKey);
            }
            else
            {
                await _peerConnectionRepository.UpdateDirectMessagePublicKeyAsync(new NetworkPeerId(peerId.Value), directMessagePublicKey);
            }

            // 4. Correctly complete the handshake on retry
            var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);

            var conversation = new ChatConversation(
                new ChatConversationId(Guid.NewGuid()),
                new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                new List<ChatParticipantId> { new(_activeIdentityContext.Identity!.Id), new(peerId.Value) },
                new List<Message>(),
                peerName);

            await _conversationRepository.AddAsync(conversation);

            await _sessionManager.EstablishSessionAsInitiatorAsync(
                new SessionConversationId(conversation.Id.Value),
                new SessionPeerId(peerId.Value),
                new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
                new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                new SessionSharedSecret(sharedSecret.Value));

            _logger.LogInformation("Successfully established session and created conversation {ConversationId}", conversation.Id.Value);

            return conversation.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish retry connection with peer {PeerName} at {Endpoint}", peerName, endpoint);
            throw;
        }
    }
    
    protected virtual async Task<EstablishSessionResponse> PerformRetryConnectionAsync(
        DnsEndPoint endpoint, 
        X509Certificate2 remoteCert,
        EstablishSessionRequest request)
    {
        // Create a handler that accepts the specific certificate
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
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
        var httpClient = new HttpClient(handler);
        
        // Set the base address and timeout
        httpClient.BaseAddress = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        
        var retryChannelOptions = new GrpcChannelOptions 
        {
            HttpClient = httpClient,
            DisposeHttpClient = true,
            ThrowOperationCanceledOnCancellation = true,
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null
        };
        
        _logger.LogInformation("TOFU: Creating retry channel with trusted certificate for {Endpoint}", endpoint);
        using var retryChannel = GrpcChannel.ForAddress($"https://{endpoint.Host}:{endpoint.Port}", retryChannelOptions);
        var retryClient = new TransportService.TransportServiceClient(retryChannel);

        _logger.LogInformation("TOFU: Sending retry EstablishSession request to {Endpoint}", endpoint);
        // Use a longer timeout for the retry connection
        var retryCallOptions = new CallOptions(deadline: DateTime.UtcNow.AddSeconds(30));
        return await retryClient.EstablishSessionAsync(request, retryCallOptions);
    }

    private async Task<Percolator.Identity.Peer> CreatePeerAsync(
        string peerName,
        EstablishSessionResponse response, 
        DnsEndPoint endpoint)
    {
        // Create the new peer
        var identityPeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var peer = new Percolator.Identity.Peer(identityPeerId, peerName);
        await _peerRepository.AddAsync(peer);

        // Create and associate a peer connection with the new peer
        var directMessagePublicKey = new DirectMessagePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray());
        var peerConnection = new PeerConnection(
            new NetworkPeerId(peer.Id.Value),
            directMessagePublicKey,
            new[] { new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow) },
            Array.Empty<TlsCertificate>(),
            DateTimeOffset.UtcNow);

        await _peerConnectionRepository.SaveAsync(peerConnection);

        return peer;
    }

    public Task<ChatConversationId?> FindExistingConversationAsync(Percolator.Identity.PeerId peerId)
    {
        //todo: create Session domain peer that tracks last active conversation
        return Task.FromResult<ChatConversationId?>(null);
    }
}