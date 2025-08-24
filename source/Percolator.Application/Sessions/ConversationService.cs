using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Identity;
using IdentityPeer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;
using Percolator.Network;
using System.Net;
using Google.Protobuf;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using CryptoSignature = Percolator.Cryptography.Signature;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using NetworkPeerId = Percolator.Network.PeerId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

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
        private readonly IOptions<TransportOptions> _transportOptions;

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
            IPeerConnectionRepository peerConnectionRepository,
            IOptions<TransportOptions> transportOptions)
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
            _transportOptions = transportOptions;
        }

        

        public async Task<ConversationId?> GetExistingDirectConversationAsync(
            DnsEndPoint endpoint,
            string remotePeerName)
        {
            var remotePeer = await _peerRepository.GetByNameAsync(remotePeerName);
            if (remotePeer == null)
            {
                _logger.LogInformation("Didn't find peer {PeerName}", remotePeerName);
                return null;
            }

            var peerConnection = await _peerConnectionRepository.GetByIdAsync(new NetworkPeerId(remotePeer.Id.Value));
            if (peerConnection is null)
            {
                _logger.LogInformation("Didn't find connection info for Peer {PeerName} with ID {PeerId}", remotePeerName, remotePeer.Id.Value);
                return null;
            }

            if (peerConnection is { IdentitySigningKey: not null })
            {
                var conversation = await _conversationRepository.GetByChannelIdAsync(new ChannelId(peerConnection.IdentitySigningKey.Value));
                if (conversation == null)
                {
                    _logger.LogInformation("Didn't find direct conversation with {PeerName} with channel ID {ChannelId}", remotePeerName, Convert.ToBase64String(peerConnection.IdentitySigningKey.Value));
                    return null;
                }

                _logger.LogInformation("Existing conversation with {PeerName} found. Reusing conversation {ConversationId}", remotePeerName, conversation.Id.Value);
                return conversation.Id;
            }

            return null;
        }

        public async Task<ConversationId> CreateNewDirectConversationAsync(
            DnsEndPoint endpoint,
            string remotePeerName)
        {
            var remotePeer = await _peerRepository.GetByNameAsync(remotePeerName);
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
                var directPayload = new EstablishDirectSessionRequest.Types.DirectInitiatorPayload
                {
                    CallbackPort = (uint)_transportOptions.Value.GrpcPort,
                    SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes)
                };

                var signedPayload = directPayload.ToByteString();
                // Prepare handshake request
                var signedPayloadBytes = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey,
                    new PreKey(signedPayload.ToByteArray()));

                // Log the key formats being used
                _logger.LogDebug(
                    "Initiating handshake with keys - SignedPreKey length: {Length}, Signature length: {SigLength}",
                    signedPreKeyPublicBytes.Length, signedPayloadBytes.Value.Length);

                var request = new EstablishDirectSessionRequest
                {
                    InitiatorBundle = new ContractsPreKeyBundle
                    {
                        IdentityAgreementKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys
                            .IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        SignedPayload = signedPayload,
                        IdentitySigningKey = Google.Protobuf.ByteString.CopyFrom(_activeIdentityContext.Keys
                            .IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                        OneTimePreKey = oneTimePreKey is null
                            ? ByteString.Empty
                            : Google.Protobuf.ByteString.CopyFrom(oneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()),
                        PayloadSignature = ByteString.CopyFrom(signedPayloadBytes.Value)
                    }
                };

                _logger.LogInformation("Sending session request to {Endpoint}", endpoint);
                var message = await _grpcSessionService.EstablishDirectSessionAsync(endpoint, request);
                if (message.MessageCase != EstablishDirectSessionResponse.MessageOneofCase.Response)
                {
                    //todo: handle not until
                    throw new InvalidOperationException($"Invalid response type:{message.MessageCase}");
                }
                var response = message.Response!;
                _logger.LogInformation("Received session response from peer");

                if (!_x3DhManager.VerifySignature(
                        new RatchetIdentityKey(response.IdentitySigningKey.ToByteArray()),
                        new PreKey(response.ResponsePayload.ToByteArray()),
                        new CryptoSignature(response.PayloadSignature.ToByteArray())))
                {
                    throw new InvalidOperationException("Invalid signature in response.");
                }
                var responderPayload = EstablishDirectSessionResponse.Types.ResponsePayload.Parser.ParseFrom(response.ResponsePayload.ToByteArray());

                var handshakeResult = _orchestrator.CompleteHandshake(
                    new RatchetIdentityKey(response.IdentitySigningKey.ToByteArray()),
                    new RatchetEphemeralKey(responderPayload.EphemeralKey.ToByteArray()),
                    oneTimePreKey
                );
                _logger.LogInformation("Handshake completed locally as Responder");

                // Get or create the peer
                if (remotePeer == null)
                {
                    remotePeer = await CreatePeerAsync(remotePeerName, handshakeResult.ResponderBundle, endpoint);
                }
                else
                {
                    // Upsert connection details even when the peer already existed
                    var netPeerId = new NetworkPeerId(remotePeer.Id.Value);
                    var peerConnection = await _peerConnectionRepository.GetByIdAsync(netPeerId);
                    if (peerConnection is null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        peerConnection = new PeerConnection(
                            netPeerId,
                            new DirectMessagePublicKey(handshakeResult.ResponderBundle.IdentitySigningKey.Value),
                            new List<GrpcEndPoint> { new(endpoint, now) },
                            Array.Empty<TlsCertificate>(),
                            now);
                        await _peerConnectionRepository.SaveAsync(peerConnection);
                    }
                    else
                    {
                        // Ensure identity key and endpoint are populated
                        if (peerConnection.IdentitySigningKey is null ||
                            !peerConnection.IdentitySigningKey.Value.SequenceEqual(handshakeResult.ResponderBundle.IdentitySigningKey.Value))
                        {
                            peerConnection.SetDirectMessagePublicKey(new DirectMessagePublicKey(handshakeResult.ResponderBundle.IdentitySigningKey.Value));
                        }
                        if (!peerConnection.GrpcEndPoints.Any(e => e.EndPoint.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && e.EndPoint.Port == endpoint.Port))
                        {
                            peerConnection.AddGrpcEndPoint(new GrpcEndPoint(endpoint, DateTimeOffset.UtcNow));
                        }
                        await _peerConnectionRepository.SaveAsync(peerConnection);
                    }
                }

                // Create a new conversation
                var conversation = new ChatConversation(
                    new ChatConversationId(Guid.Parse(responderPayload.SessionId)),
                    new ChannelId(handshakeResult.ResponderBundle.IdentitySigningKey.Value),
                    new List<ChatParticipantId> { new(_activeIdentityContext.Identity!.Id), new(remotePeer.Id.Value) },
                    new List<Message>(),
                    remotePeerName);

                _logger.LogInformation("Creating conversation {ConversationId} with channel ID {ChannelId}", conversation.Id.Value, Convert.ToBase64String(conversation.ChannelId.Value));
                await _conversationRepository.AddAsync(conversation);

                await _sessionManager.EstablishSessionAsResponderAsync(
                    new SessionId(conversation.Id.Value),
                    new Percolator.Identity.PeerId(remotePeer.Id.Value),
                    handshakeResult.ResponderBundle.IdentitySigningKey, // Alice's Public Identity Key
                    new RatchetEphemeralKey(responderPayload.EphemeralKey.ToByteArray()), // Alice's Public Ratchet Key
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
                _logger.LogError(ex, "Failed to establish direct conversation with {PeerName}", remotePeerName);
                throw;
            }
        }

        private async Task<Peer> CreatePeerAsync(string name, X3dPreKeyBundle preKeyBundle, DnsEndPoint endpoint)
        {
            var newPeer = new IdentityPeer(new IdentityPeerId(Guid.NewGuid()), name);
            await _peerRepository.AddAsync(newPeer);
            var networkPeerId = new NetworkPeerId(newPeer.Id.Value);
            var timeStamp=DateTime.UtcNow;
            var peerConnection = new PeerConnection(
                networkPeerId, 
                new DirectMessagePublicKey( preKeyBundle.IdentitySigningKey.Value), 
                new List<GrpcEndPoint>{new GrpcEndPoint(endpoint,timeStamp)},
                new List<TlsCertificate>(),timeStamp);
            await _peerConnectionRepository.SaveAsync(peerConnection);
            return newPeer;
        }
    }
}