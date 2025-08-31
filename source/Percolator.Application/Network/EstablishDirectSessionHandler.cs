using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using IdentityPeer = Percolator.Identity.Peer;
using IdentityPeerId = Percolator.Identity.PeerId;
using NetworkPeerId = Percolator.Network.PeerId;
using CryptoSignature = Percolator.Cryptography.Signature;

namespace Percolator.Application.Network
{
    public sealed class EstablishDirectSessionHandler : IRequestHandler<EstablishDirectSessionCommand, EstablishDirectSessionResult>
    {
        private readonly ILogger<EstablishDirectSessionHandler> _logger;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IX3DHOrchestrator _x3dhOrchestrator;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IConversationRepository _conversationRepository;
        private readonly IPeerRepository _peerRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IX3DHManager _x3DhManager;
        private readonly IDirectSessionRepository _directSessionRepository;

        public EstablishDirectSessionHandler(
            ILogger<EstablishDirectSessionHandler> logger,
            ActiveIdentityContext activeIdentityContext,
            IX3DHOrchestrator x3dhOrchestrator,
            IDirectSessionManager sessionManager,
            IConversationRepository conversationRepository,
            IPeerRepository peerRepository,
            IPeerConnectionRepository peerConnectionRepository,
            IX3DHManager x3DhManager,
            IDirectSessionRepository directSessionRepository)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _conversationRepository = conversationRepository;
            _peerRepository = peerRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _x3DhManager = x3DhManager;
            _directSessionRepository = directSessionRepository;
        }

        public async Task<EstablishDirectSessionResult> Handle(EstablishDirectSessionCommand request, CancellationToken cancellationToken)
        {
            // Validate active identity
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("Local peer identity has not been established. Cannot respond to handshake");
                throw new InvalidOperationException("Server identity not initialized.");
            }

            // Build identity/signature inputs
            var remoteIdentityKey = new RatchetIdentityKey(request.IdentitySigningKeyBytes);
            var requestPayloadSignature = new CryptoSignature(request.PayloadSignatureBytes);
            var requestPayload = new PreKey(request.SignedPayloadBytes);

            // Verify the signed pre-key payload
            if (!_x3DhManager.VerifySignature(remoteIdentityKey, requestPayload, requestPayloadSignature))
            {
                throw new CryptographicException("Invalid signature on signed pre-key.");
            }
            _logger.LogDebug("Signature verification successful");

            // Persist/update peer connection info based on endpoint
            var networkIdentitySigningKey = new DirectMessagePublicKey(request.IdentitySigningKeyBytes);
            var timestamp = DateTimeOffset.Now;

            var peerConnectionInfo = await _peerConnectionRepository.GetByDirectMessage(networkIdentitySigningKey);
            if (peerConnectionInfo is null)
            {
                _logger.LogWarning("No connection info found for peer {DirectMessagePublicKey}. Creating a new connection record", Convert.ToBase64String(networkIdentitySigningKey.Value));
                var grpcEndPoint = new GrpcEndPoint(request.PeerEndPoint, timestamp);
                peerConnectionInfo = new PeerConnection(
                    new NetworkPeerId(Guid.NewGuid()),
                    networkIdentitySigningKey,
                    new[] { grpcEndPoint },
                    new List<TlsCertificate>(),
                    timestamp);
            }
            else
            {
                var grpcEndPoint = peerConnectionInfo.GrpcEndPoints.FirstOrDefault(e => e.EndPoint.Equals(request.PeerEndPoint));
                if (grpcEndPoint is null)
                {
                    _logger.LogWarning("No gRPC endpoints found for peer {DirectMessagePublicKey}. Adding a new one", Convert.ToBase64String(networkIdentitySigningKey.Value));
                    peerConnectionInfo.AddGrpcEndPoint(new GrpcEndPoint(request.PeerEndPoint, timestamp));
                }
                else
                {
                    peerConnectionInfo.UpdateLastSeen(grpcEndPoint, timestamp);
                }
            }

            // Derive shared secret (Initiator)
            _logger.LogInformation("Processing X3DH handshake with initiator bundle. Examining bundle properties...");
            var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            var prekeyBundle = new X3dPreKeyBundle(
                remoteIdentityKey,
                new RatchetAgreementKey(request.IdentityAgreementKeyBytes),
                new PreKey(request.PreKeyBytes),
                request.OneTimePreKeyBytes is not null ? new OneTimeKey(request.OneTimePreKeyBytes) : null);

            var sharedSecret = _x3dhOrchestrator.InitiateHandshake(prekeyBundle, ephemeralKey);
            _logger.LogInformation("X3DH handshake processed successfully as Initiator");

            var identityPeerId = new IdentityPeerId(peerConnectionInfo.Id.Value);
            var remotePeer = await _peerRepository.GetByIdAsync(identityPeerId);
            if (remotePeer is null)
            {
                _logger.LogInformation("Peer with key hash {KeyHash} is unknown. Creating a new peer record", Convert.ToBase64String(request.IdentitySigningKeyBytes));
                var newPeerName = $"Peer-{Convert.ToBase64String(request.IdentitySigningKeyBytes)}";
                remotePeer = new IdentityPeer(identityPeerId, newPeerName);
                await _peerRepository.AddAsync(remotePeer);
            }

            // Now that the Peer exists, persist/update the PeerConnection
            await _peerConnectionRepository.SaveAsync(peerConnectionInfo);

            // Create conversation (channel) if absent and establish session
            var channelId = new ChannelId(networkIdentitySigningKey.Value);
            var conversation = await _conversationRepository.GetByChannelIdAsync(channelId, _activeIdentityContext.Identity!.SelfIdentityId);

            if (conversation is null)
            {
                var participants = new List<ChatParticipantId>
                {
                    new(_activeIdentityContext.Identity.Id),
                    new(remotePeer.Id.Value)
                };

                _logger.LogInformation("Creating new conversation with participants  {Participants} channel ID {ChannelId}", string.Join(", ", participants), Convert.ToBase64String(channelId.Value));

                conversation = new ChatConversation(
                    ChatConversationId.NewId(),
                    channelId,
                    participants,
                    new List<Message>(),
                    remotePeer.Name);

                await _directSessionRepository.UpsertAsync(new NetworkPeerId(remotePeer.Id.Value), new DirectSessionId(conversation.Id.Value), _activeIdentityContext.Identity!.SelfIdentityId);
                await _conversationRepository.AddAsync(conversation, _activeIdentityContext.Identity!.SelfIdentityId);
                _logger.LogInformation("Created new conversation with peer {PeerName}", remotePeer.Name);

                var cryptoSessionId = new SessionId(conversation.Id.Value);
                await _sessionManager.EstablishSessionAsInitiatorAsync(
                    cryptoSessionId,
                    identityPeerId,
                    remoteIdentityKey,
                    new RatchetEphemeralKey(request.PreKeyBytes),
                    sharedSecret,
                    ephemeralKey);
                _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", conversation.Id, remotePeer.Id);
            }

            // Build response payload and sign
            var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
            {
                EphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                SessionId = conversation!.Id.ToString()
            }.ToByteString();

            var signedPayloadBytes = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey, new PreKey(responsePayload.ToByteArray()));

            return new EstablishDirectSessionResult
            {
                SessionId = conversation.Id.ToString(),
                ResponsePayloadBytes = responsePayload.ToByteArray(),
                IdentitySigningKeyBytes = _activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo(),
                PayloadSignatureBytes = signedPayloadBytes.Value
            };
        }
    }
}
