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

        public async Task<ConversationId> CreateDirectConversationAsync(
            DnsEndPoint endpoint, 
            string remotePeerName)
        {
            var remotePeer = await _peerRepository.GetByNameAsync(remotePeerName);
            if (remotePeer == null)
            {
                _logger.LogInformation("Didn't find peer {PeerName}", remotePeerName);
            }
            else {
                var peerConnection = await _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(remotePeer.Id.Value));
                if (peerConnection is null)
                {
                    _logger.LogInformation("Didn't find connection info for Peer {PeerName} with ID {PeerId}", remotePeerName, remotePeer.Id.Value);
                }
                if (peerConnection is {DirectMessagePublicKey: not null})
                {
                    var conversation = await _conversationRepository.GetByChannelIdAsync(new ChannelId(peerConnection.DirectMessagePublicKey.Value));
                    if (conversation == null)
                    {
                        _logger.LogInformation("Didn't find direct conversation with {PeerName} with channel ID {ChannelId}", remotePeerName, Convert.ToBase64String(peerConnection.DirectMessagePublicKey.Value));
                    }
                    else {
                        _logger.LogInformation("Existing conversation with {PeerName} found. Reusing conversation {ConversationId}", remotePeerName, conversation.Id.Value);
                        // The session already exists, so just return the ID.
                        return conversation.Id;
                    }
                }
            }
            try
            {
                _logger.LogInformation("Creating direct conversation with {PeerName} at {Endpoint}", remotePeerName,
                    endpoint);

                // Create ephemeral key for this handshake
                var oneTimePreKey = _oneTimeKeyProvider.PopOneTimeKey();

                // Verify identity and keys are available
                if (_activeIdentityContext.Identity == null || _activeIdentityContext.Keys == null)
                {
                    _logger.LogError("No active identity or keys available");
                    throw new InvalidOperationException("No active identity or keys available");
                }

                var signedPreKeyPublicBytes =
                    _activeIdentityContext.Keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
                // Prepare handshake request
                var signPreKey = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey,
                    new PreKey(signedPreKeyPublicBytes));

                // Log the key formats being used
                _logger.LogDebug(
                    "Initiating handshake with keys - SignedPreKey length: {Length}, Signature length: {SigLength}",
                    signedPreKeyPublicBytes.Length, signPreKey.Value.Length);

                var request = new EstablishSessionRequest
                {
                    InitiatorBundle = new ContractsPreKeyBundle
                    {
                        IdentityAgreementKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys
                            .IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        SignedPreKey = Google.Protobuf.ByteString.CopyFrom(signedPreKeyPublicBytes),
                        IdentitySigningKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys
                            .IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                        OneTimePreKey = oneTimePreKey is null
                            ? ByteString.Empty
                            : Google.Protobuf.ByteString.CopyFrom(oneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        PreKeySignature = ByteString.CopyFrom(signPreKey.Value)
                    }
                };

                _logger.LogInformation("Sending session request to {Endpoint}", endpoint);
                var response = await _grpcSessionService.EstablishSessionAsync(endpoint, request);
                _logger.LogInformation("Received session response from peer.");

                var handshakeResult = _orchestrator.CompleteHandshake(
                    response.ResponderBundle, // Alice's bundle from the response
                    response.ResponderBundle.SignedPreKey.ToByteArray(), // Alice's ephemeral key from the response
                    oneTimePreKey
                );
                _logger.LogInformation("Handshake completed locally as Responder.");

                // Get or create the peer
                if (remotePeer == null)
                {
                    remotePeer = await CreatePeerAsync(remotePeerName, response.ResponderBundle, endpoint);
                }

                // Create a new conversation
                var conversation = new ChatConversation(
                    new ChatConversationId(Guid.Parse(response.SessionId)),
                    new ChannelId(response.ResponderBundle.IdentitySigningKey.ToByteArray()),
                    new List<ChatParticipantId> {new(_activeIdentityContext.Identity!.Id), new(remotePeer.Id.Value)},
                    new List<Message>(),
                    remotePeerName);

                _logger.LogInformation("Creating conversation {ConversationId} with channel ID {ChannelId}", conversation.Id.Value, Convert.ToBase64String(conversation.ChannelId.Value));
                await _conversationRepository.AddAsync(conversation);

                await _sessionManager.EstablishSessionAsResponderAsync(
                    new SessionId(conversation.Id.Value),
                    new Percolator.Identity.PeerId(remotePeer.Id.Value),
                    new RatchetIdentityKey(response.ResponderBundle.IdentityAgreementKey
                        .ToByteArray()), // Alice's Public Identity Key
                    new RatchetEphemeralKey(response.ResponderBundle.SignedPreKey
                        .ToByteArray()), // Alice's Public Ratchet Key
                    handshakeResult
                        .ResponderPrivateKeyUsed, // The specific one of OUR (Bob's) private keys that was used
                    new SharedSecret(handshakeResult.SharedSecret.Value)
                );

                _logger.LogInformation("Successfully established session and created conversation {ConversationId}",
                    conversation.Id);

                return conversation.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to establish secure connection");
                throw new InvalidOperationException("Failed to establish secure connection", ex);
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