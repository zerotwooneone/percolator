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
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
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
        private readonly IOneTimeKeyProvider _oneTimeKeyProvider;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IGrpcSessionService _grpcSessionService;
        private readonly IX3DHManager _x3DhManager;
        private readonly IPeerConnectionRepository _peerConnectionRepository;

        public ConversationService(
            ILogger<ConversationService> logger,
            IX3DHOrchestrator orchestrator,
            IDirectSessionManager sessionManager,
            IConversationRepository conversationRepository,
            IPeerRepository peerRepository,
            IOneTimeKeyProvider oneTimeKeyProvider,
            ActiveIdentityContext activeIdentityContext,
            IGrpcSessionService grpcSessionService, 
            IX3DHManager x3DhManager, 
            IPeerConnectionRepository peerConnectionRepository)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _sessionManager = sessionManager;
            _conversationRepository = conversationRepository;
            _peerRepository = peerRepository;
            _oneTimeKeyProvider = oneTimeKeyProvider;
            _activeIdentityContext = activeIdentityContext;
            _grpcSessionService = grpcSessionService;
            _x3DhManager = x3DhManager;
            _peerConnectionRepository = peerConnectionRepository;
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
        
        private async Task<ChatConversationId?> CreateDirectConversationWithKeyAsync(
            DnsEndPoint endpoint, 
            string peerName)
        {
            try
            {
                _logger.LogInformation("Creating direct conversation with {PeerName} at {Endpoint}", peerName, endpoint);
                
                // Create ephemeral key for this handshake
                var ephemeralKey = _oneTimeKeyProvider.PopOneTimeKey();
                
                // Verify identity and keys are available
                if (_activeIdentityContext.Identity == null || _activeIdentityContext.Keys == null)
                {
                    _logger.LogError("No active identity or keys available");
                    throw new InvalidOperationException("No active identity or keys available");
                }
                var signedPreKeyPublicBytes = _activeIdentityContext.Keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
                // Prepare handshake request
                var signPreKey = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey, new PreKey(signedPreKeyPublicBytes));
                
                // Log the key formats being used
                _logger.LogDebug("Initiating handshake with keys - SignedPreKey length: {Length}, Signature length: {SigLength}",
                    signedPreKeyPublicBytes.Length, signPreKey.Value.Length);
                
                var request = new EstablishSessionRequest
                {
                    InitiatorBundle = new ContractsPreKeyBundle
                    {
                        // Use consistent key export format for all keys - SubjectPublicKeyInfo
                        IdentityAgreementKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        SignedPreKey = Google.Protobuf.ByteString.CopyFrom(signedPreKeyPublicBytes),
                        IdentitySigningKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                        OneTimePreKey = ephemeralKey is null ? ByteString.Empty : Google.Protobuf.ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        PreKeySignature = ByteString.CopyFrom(signPreKey.Value)
                    },
                    InitiatorEphemeralKey = ephemeralKey is null ? ByteString.Empty : Google.Protobuf.ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo())
                };
                
                _logger.LogDebug("Created handshake request with valid signature");
                
                _logger.LogInformation("Created establish session request with valid signature for X3DH handshake");
                
                // For shared certificate approach, we don't need TOFU flow or special cert handling
                // We know both sides use the same certificate, so skip all the cert verification logic
                
                _logger.LogInformation("Using shared certificate approach for {Endpoint}", endpoint);
                
                // Direct connection using shared certificate
                var response = await _grpcSessionService.EstablishSessionAsync(endpoint, request);
                
                _logger.LogInformation("Successfully connected using shared certificate");
                
                // Complete the handshake
                var sharedSecret = _orchestrator.CompleteHandshake(response.ResponderBundle, ephemeralKey);
                
                // Get or create the peer
                var peer = await _peerRepository.GetByNameAsync(peerName);
                if (peer == null)
                {
                    peer = await CreatePeerAsync(peerName, response.ResponderBundle, endpoint);
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
                _logger.LogError(ex, "Failed to establish secure connection using shared certificate");
                throw new InvalidOperationException("Failed to establish secure connection using shared certificate", ex);
            }
        }
        
        private async Task<IdentityPeer> CreatePeerAsync(string peerName,
            ContractsPreKeyBundle responderBundle, DnsEndPoint endpoint)
        {
            var newPeer = new IdentityPeer(new IdentityPeerId(Guid.NewGuid()), peerName);
            await _peerRepository.AddAsync(newPeer);
            var networkPeerId = new NetworkPeerId(newPeer.Id.Value);
            var timeStamp=DateTime.UtcNow;
            var peerConnection = new PeerConnection(
                networkPeerId, 
                new DirectMessagePublicKey( responderBundle.IdentityAgreementKey.ToByteArray()), 
                new List<GrpcEndPoint>{new GrpcEndPoint(endpoint,timeStamp)},
                new List<TlsCertificate>(),timeStamp);
            await _peerConnectionRepository.SaveAsync(peerConnection);
            return newPeer;
        }
    }
}