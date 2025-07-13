using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Sessions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Chat.ValueObjects;

using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using NetworkPeerId = Percolator.Network.PeerId;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionIdentityKey = Percolator.Sessions.SessionIdentityKey;
using SessionRatchetKey = Percolator.Sessions.SessionRatchetKey;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using IdentityPeer = Percolator.Identity.Peer;

namespace Percolator.Application.Sessions
{
    internal class ConversationService : IConversationService
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
        private readonly ITlsHandshakeService _tlsHandshakeService;
        private readonly IGrpcSessionService _grpcSessionService;
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
            ITlsHandshakeService tlsHandshakeService,
            IGrpcSessionService grpcSessionService,
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
            _tlsHandshakeService = tlsHandshakeService;
            _grpcSessionService = grpcSessionService;
            _peerTrustManager = peerTrustManager;
        }

        public async Task<ChatConversationId> CreateDirectConversationAsync(DnsEndPoint endpoint, string peerName)
        {
            _logger.LogInformation("Creating direct conversation with peer {PeerName} at {Endpoint}", peerName, endpoint);
            var result = await CreateDirectConversationWithKeyAsync(endpoint, peerName);
            
            if (result == null)
            {
                throw new InvalidOperationException($"Failed to create conversation with peer {peerName}");
            }
            
            return result.Value;
        }

        public async Task<ChatConversationId> CreateDirectConversationAsync(DnsEndPoint endpoint, string peerName, byte[] remoteTlsKey)
        {
            _logger.LogInformation("Creating direct conversation with peer {PeerName} at {Endpoint} with provided TLS key", peerName, endpoint);
            var result = await CreateDirectConversationWithKeyAsync(endpoint, peerName, remoteTlsKey);
            
            if (result == null)
            {
                throw new InvalidOperationException($"Failed to create conversation with peer {peerName}");
            }
            
            return result.Value;
        }
        
        private async Task<ChatConversationId?> CreateDirectConversationWithKeyAsync(
            DnsEndPoint endpoint, 
            string peerName, 
            byte[]? remoteTlsKey = null)
        {
            try
            {
                if (_activeIdentityContext.Identity == null || _activeIdentityContext.Keys == null)
                {
                    _logger.LogError("No active identity available");
                    throw new InvalidOperationException("No active identity available");
                }

                // 1. Create a one-time key for this session
                var ephemeralKey = _oneTimeKeyProvider.PopOneTimeKey();
                if (ephemeralKey == null)
                {
                    _logger.LogError("Failed to obtain one-time key");
                    throw new InvalidOperationException("Failed to obtain one-time key");
                }

                var request = new EstablishSessionRequest
                {
                    InitiatorBundle = new ContractsPreKeyBundle
                    {
                        IdentityAgreementKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        SignedPreKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        IdentitySigningKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                        OneTimePreKey = Google.Protobuf.ByteString.CopyFrom()
                    }
                };

                if (remoteTlsKey != null)
                {
                    // If a TLS key is provided, use it for authentication
                    return await HandlePinnedCertificateAsync(endpoint, peerName, remoteTlsKey, request, ephemeralKey);
                }
                else
                {
                    // Regular TOFU flow - attempt connection, capture cert on failure, then retry with the cert
                    return await HandleRegularTofuAsync(endpoint, peerName, request, ephemeralKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish secure connection during Trust-on-First-Use");
                throw new InvalidOperationException("Failed to establish secure connection during Trust-on-First-Use", ex);
            }
        }
        
        private async Task<ChatConversationId?> HandleRegularTofuAsync(
            DnsEndPoint endpoint, 
            string peerName, 
            EstablishSessionRequest request, 
            ECDiffieHellman ephemeralKey)
        {
            try
            {
                // First attempt - will likely fail with certificate validation error
                _logger.LogInformation("Attempting initial connection to {Endpoint}", endpoint);
                var response = await _grpcSessionService.EstablishSessionAsync(endpoint, request);
                
                _logger.LogInformation("Successfully connected on first attempt without TOFU");
                
                // If we got here, the connection succeeded without TOFU
                var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);
                
                var peer = await _peerRepository.GetByNameAsync(peerName);
                if (peer == null)
                {
                    peer = await CreatePeerAsync(peerName, response.ResponderBundle);
                }

                // Create a new conversation
                var conversation = new ChatConversation(
                    new ChatConversationId(Guid.NewGuid()),
                    new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                    new List<ChatParticipantId> { new(_activeIdentityContext.Identity!.Id), new(peer.Id.Value) },
                    new List<Message>(),
                    peerName);

                await _conversationRepository.AddAsync(conversation);

                // Establish a session with the peer
                await _sessionManager.EstablishSessionAsInitiatorAsync(
                    new SessionConversationId(conversation.Id.Value),
                    new SessionPeerId(peer.Id.Value),
                    new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
                    new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                    new SessionSharedSecret(sharedSecret.Value));

                _logger.LogInformation("Successfully established session and created conversation {ConversationId}", conversation.Id);

                return conversation.Id;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Initial connection failed as expected during TOFU process. Attempting to capture certificate.");
                
                // Capture the certificate for TOFU
                var remoteCert = await _tlsHandshakeService.CaptureCertificateAsync(endpoint);
                
                if (remoteCert == null)
                {
                    _logger.LogError("Failed to capture remote certificate during TOFU");
                    throw new InvalidOperationException("Failed to capture certificate during TOFU process", ex);
                }

                // Now we have the certificate, try to establish a connection with it
                return await HandleTofuWithCapturedCertificateAsync(endpoint, peerName, remoteCert, request, ephemeralKey);
            }
        }
        
        private async Task<ChatConversationId?> HandleTofuWithCapturedCertificateAsync(
            DnsEndPoint endpoint,
            string peerName,
            X509Certificate2 remoteCert,
            EstablishSessionRequest request,
            ECDiffieHellman ephemeralKey)
        {
            // Check if we've seen this certificate before
            _logger.LogInformation("Looking up certificate with thumbprint {Thumbprint}", remoteCert.Thumbprint);
            
            var existingConnection = await _peerConnectionRepository.GetByTlsCertificateAsync(
                new TlsCertificate(remoteCert.RawData));
                
            if (existingConnection != null)
            {
                _logger.LogInformation("Found existing peer connection for certificate {Thumbprint}", remoteCert.Thumbprint);
                
                // We've seen this certificate before, get the associated peer
                var peer = await _peerRepository.GetByIdAsync(new IdentityPeerId(existingConnection.Id.Value));
                if (peer == null)
                {
                    throw new InvalidOperationException("Peer connection exists but peer not found.");
                }
                
                // We trust this peer, establish a session
                _logger.LogInformation("Establishing session with known peer {PeerName} with TOFU", peerName);
                return await RetryConnectionWithTrustedCertAsync(endpoint, peerName, remoteCert, request, ephemeralKey, peer.Id, existingConnection);
            }
            else
            {
                _logger.LogInformation("No existing peer connection found for certificate {Thumbprint}, creating new peer", remoteCert.Thumbprint);
                
                // First-time connection, create a peer
                var newPeerId = new IdentityPeerId(Guid.NewGuid());
                var newPeer = new IdentityPeer(newPeerId, peerName);
                await _peerRepository.AddAsync(newPeer);
                
                // Create a peer connection with the captured certificate
                var newConnection = new PeerConnection(
                    new NetworkPeerId(newPeerId.Value),
                    null, // DirectMessagePublicKey will be populated after handshake
                    new[] { new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow) },
                    new[] { new TlsCertificate(remoteCert.RawData) },
                    DateTimeOffset.UtcNow
                );
                
                await _peerConnectionRepository.SaveAsync(newConnection);
                _logger.LogInformation("Created new peer {PeerId} and connection for {Endpoint}", newPeerId.Value, endpoint);
                
                return await RetryConnectionWithTrustedCertAsync(endpoint, peerName, remoteCert, request, ephemeralKey, newPeerId, newConnection);
            }
        }
        
        private async Task<ChatConversationId?> RetryConnectionWithTrustedCertAsync(
            DnsEndPoint endpoint, 
            string peerName, 
            X509Certificate2 remoteCert,
            EstablishSessionRequest request,
            ECDiffieHellman ephemeralKey,
            IdentityPeerId peerId,
            PeerConnection? existingConnection = null)
        {
            // Create a fresh copy of the certificate to use for the trusted connection
            byte[] certData = remoteCert.Export(X509ContentType.Cert);
            using var freshCertificate = new X509Certificate2(certData);
            
            // Add the certificate to the trusted certs for this request
            await _peerTrustManager.AddTrustedPeer(freshCertificate);
            
            const int MaxRetries = 3;
            const int RetryDelayMs = 1000;
            
            EstablishSessionResponse? response = null;
            Exception? lastException = null;
            
            // Implement retry mechanism with delay
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try 
                {
                    if (attempt > 0)
                    {
                        _logger.LogInformation("TOFU: Retrying connection attempt {Attempt} of {MaxRetries} with {Endpoint} after delay", 
                            attempt + 1, MaxRetries, endpoint);
                        // Add delay between retries to allow certificate store to process
                        await Task.Delay(RetryDelayMs * attempt);
                    }
                    
                    // Try to establish the session
                    response = await _grpcSessionService.EstablishSessionAsync(endpoint, request, freshCertificate);
                    
                    if (response != null)
                    {
                        _logger.LogInformation("TOFU: Successfully received EstablishSession response from {Endpoint} on attempt {Attempt}", 
                            endpoint, attempt + 1);
                        break; // Success, exit the retry loop
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "TOFU: Attempt {Attempt} of {MaxRetries} to establish connection with {Endpoint} failed", 
                        attempt + 1, MaxRetries, endpoint);
                    
                    // If this is the last attempt, we'll throw later after cleanup
                    if (attempt < MaxRetries - 1)
                    {
                        continue; // Try again
                    }
                }
            }
            
            // If we never got a response after all retries, throw the last exception
            if (response == null)
            {
                _logger.LogError(lastException, "Failed to establish secure connection with peer {PeerName} at {Endpoint} after {MaxRetries} attempts", 
                    peerName, endpoint, MaxRetries);
                throw new RpcException(new Status(StatusCode.Unavailable, "Failed to establish secure connection after multiple attempts"), 
                    $"Failed to establish connection with peer {peerName}");
            }
            
            try 
            {
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

                // Complete the handshake on retry
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
                _logger.LogError(ex, "Failed to process conversation data after successful connection with peer {PeerName} at {Endpoint}", 
                    peerName, endpoint);
                throw;
            }
        }

        private async Task<ChatConversationId?> HandlePinnedCertificateAsync(
            DnsEndPoint endpoint,
            string peerName,
            byte[] remoteTlsKey,
            EstablishSessionRequest request,
            ECDiffieHellman ephemeralKey)
        {
            // Create certificate from the provided TLS key
            var cert = await _tlsCertificateService.GetOrCreateTlsCertificateAsync(peerName,remoteTlsKey);
            if (cert == null)
            {
                throw new InvalidOperationException("Failed to create certificate from provided TLS key");
            }

            _logger.LogInformation("Using provided TLS key to establish connection with {Endpoint}", endpoint);
            
            // Add the certificate to trusted list
            await _peerTrustManager.AddTrustedPeer(cert);
            
            // Look up the peer by certificate 
            var peerConnection = await _peerConnectionRepository.GetByTlsCertificateAsync(
                new TlsCertificate(cert.RawData));
            
            IdentityPeer peer;
            
            if (peerConnection == null)
            {
                _logger.LogInformation("No existing peer connection for the provided certificate, creating new peer");
                
                // Check if peer exists by name, if not create one
                peer = await _peerRepository.GetByNameAsync(peerName);
                if (peer == null)
                {
                    peer = new IdentityPeer(new IdentityPeerId(Guid.NewGuid()), peerName);
                    await _peerRepository.AddAsync(peer);
                    
                    _logger.LogInformation("Created new peer {PeerId} for {PeerName}", peer.Id.Value, peerName);
                }
                
                // Create a peer connection
                peerConnection = new PeerConnection(
                    new NetworkPeerId(peer.Id.Value),
                    null, // Will be filled in after handshake
                    new[] { new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow) },
                    new[] { new TlsCertificate(cert.RawData) },
                    DateTimeOffset.UtcNow
                );
                
                await _peerConnectionRepository.SaveAsync(peerConnection);
                _logger.LogInformation("Added new peer connection for {PeerName}", peerName);
            }
            else
            {
                _logger.LogInformation("Found existing peer connection for the provided certificate");
                peer = await _peerRepository.GetByIdAsync(new IdentityPeerId(peerConnection.Id.Value));
                if (peer == null)
                {
                    throw new InvalidOperationException("Peer connection exists but peer not found");
                }
            }
            
            // Establish a session using the provided certificate
            var response = await _grpcSessionService.EstablishSessionAsync(endpoint, request, cert);
            
            // Update the DirectMessagePublicKey
            var directMessagePublicKey = new DirectMessagePublicKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray());
            await _peerConnectionRepository.UpdateDirectMessagePublicKeyAsync(peerConnection.Id, directMessagePublicKey);
            
            // Complete the handshake
            var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);
            
            // Create a conversation
            var conversation = new ChatConversation(
                new ChatConversationId(Guid.NewGuid()),
                new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                new List<ChatParticipantId> { new(_activeIdentityContext.Identity!.Id), new(peer.Id.Value) },
                new List<Message>(),
                peerName);
                
            await _conversationRepository.AddAsync(conversation);
            
            // Establish a session
            await _sessionManager.EstablishSessionAsInitiatorAsync(
                new SessionConversationId(conversation.Id.Value),
                new SessionPeerId(peer.Id.Value),
                new SessionIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()),
                new SessionRatchetKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                new SessionSharedSecret(sharedSecret.Value));
                
            _logger.LogInformation("Successfully established session with provided TLS key and created conversation {ConversationId}", 
                conversation.Id.Value);
                
            return conversation.Id;
        }

        private async Task<IdentityPeer> CreatePeerAsync(
            string peerName,
            ContractsPreKeyBundle responderBundle)
        {
            var newPeer = new IdentityPeer(new IdentityPeerId(Guid.NewGuid()), peerName);
            await _peerRepository.AddAsync(newPeer);
            return newPeer;
        }
    }
}