using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
using System.Net;
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
                
                _logger.LogInformation("Sending session request to {Endpoint}", endpoint);
                var response = await _grpcSessionService.EstablishSessionAsync(endpoint, request);
                _logger.LogInformation("Received session response from peer.");
                
                var handshakeResult = _orchestrator.CompleteHandshake(
                    response.ResponderBundle, // Alice's bundle from the response
                    response.ResponderBundle.SignedPreKey.ToByteArray() // Alice's ephemeral key from the response
                );
                _logger.LogInformation("Handshake completed locally as Responder.");
                
                // Get or create the peer
                var peer = await _peerRepository.GetByNameAsync(peerName);
                if (peer == null)
                {
                    peer = await CreatePeerAsync(peerName, response.ResponderBundle, endpoint);
                }

                // Create a new conversation
                var conversation = new ChatConversation(
                    new ChatConversationId(Guid.Parse(response.SessionId)),
                    new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                    new List<ChatParticipantId> { new(_activeIdentityContext.Identity!.Id), new(peer.Id.Value) },
                    new List<Message>(),
                    peerName);

                await _conversationRepository.AddAsync(conversation);

                await _sessionManager.EstablishSessionAsInitiatorAsync(
                    new SessionId(conversation.Id.Value),
                    new Percolator.Identity.PeerId(peer.Id.Value),
                    new RatchetIdentityKey(response.ResponderBundle.IdentityAgreementKey.ToByteArray()), // Alice's Identity Key
                    new RatchetEphemeralKey(response.ResponderBundle.SignedPreKey.ToByteArray()),
                    new SharedSecret(handshakeResult.SharedSecret.Value),
                    ephemeralKey
                );

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