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
        private readonly IPeerRepository _peerRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IX3DHManager _x3DhManager;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IPeerPublicSigningKeyStore _pkhStore;

        public EstablishDirectSessionHandler(
            ILogger<EstablishDirectSessionHandler> logger,
            ActiveIdentityContext activeIdentityContext,
            IX3DHOrchestrator x3dhOrchestrator,
            IDirectSessionManager sessionManager,
            IPeerRepository peerRepository,
            IPeerConnectionRepository peerConnectionRepository,
            IX3DHManager x3DhManager,
            IDirectSessionRepository directSessionRepository,
            IPeerPublicSigningKeyStore pkhStore)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _peerRepository = peerRepository;
            _peerConnectionRepository = peerConnectionRepository;
            _x3DhManager = x3DhManager;
            _directSessionRepository = directSessionRepository;
            _pkhStore = pkhStore;
        }

        public async Task<EstablishDirectSessionResult> Handle(EstablishDirectSessionCommand request, CancellationToken cancellationToken)
        {
            // Validate active identity
            if (_activeIdentityContext.Identity is null || _activeIdentityContext.Keys is null)
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

            var existingPeerConnectionInfo = await _peerConnectionRepository.GetByPublicKey(networkIdentitySigningKey);
            NetworkPeerId networkPeerId;
            PeerConnection peerConnectionInfo;
            if (existingPeerConnectionInfo is null)
            {
                _logger.LogWarning("No connection info found for peer {DirectMessagePublicKey}. Creating a new connection record", Convert.ToBase64String(networkIdentitySigningKey.Value));
                var grpcEndPoint = new GrpcEndPoint(request.PeerEndPoint, timestamp);
                networkPeerId = new NetworkPeerId(Guid.NewGuid());
                peerConnectionInfo = new PeerConnection(
                    networkPeerId,
                    networkIdentitySigningKey,
                    new[] { grpcEndPoint },
                    new List<TlsCertificate>(),
                    timestamp);
            }
            else
            {
                networkPeerId = existingPeerConnectionInfo.Id;
                var grpcEndPoint = existingPeerConnectionInfo.GrpcEndPoints.FirstOrDefault(e => e.EndPoint.Equals(request.PeerEndPoint));
                if (grpcEndPoint is null)
                {
                    _logger.LogWarning("No gRPC endpoints found for peer {DirectMessagePublicKey}. Adding a new one", Convert.ToBase64String(networkIdentitySigningKey.Value));
                    existingPeerConnectionInfo.AddGrpcEndPoint(new GrpcEndPoint(request.PeerEndPoint, timestamp));
                }
                else
                {
                    existingPeerConnectionInfo.UpdateLastSeen(grpcEndPoint, timestamp);
                }
                peerConnectionInfo = existingPeerConnectionInfo;
            }

            // Derive shared secret (Initiator)
            _logger.LogInformation("Processing X3DH handshake with initiator bundle. Examining bundle properties...");
            var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            var remotePreKey = new PreKey(request.PreKeyBytes);
            var prekeyBundle = new X3dPreKeyBundle(
                remoteIdentityKey,
                remotePreKey,
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

            // Handshake-side identity mapping: bind PKH -> this peer id (idempotent if already bound to same peer)
            var initiatorSpki = request.IdentitySigningKeyBytes;
            var initiatorPkh = SHA256.HashData(initiatorSpki);
            await _pkhStore.ActivateIfChangedAsync(remotePeer.Id, initiatorSpki, initiatorPkh, DateTimeOffset.UtcNow, cancellationToken);

            // Now that the Peer exists, persist/update the PeerConnection
            await _peerConnectionRepository.SaveAsync(peerConnectionInfo);

            var existingDirectSession =
                await _directSessionRepository.GetByRemotePeerIdAsync(networkPeerId,
                    _activeIdentityContext.Identity.SelfIdentityId);
            var directSessionId = existingDirectSession?.SessionId 
                                  ?? new DirectSessionId(Guid.NewGuid());
            if (existingDirectSession is null)
            {
                await _directSessionRepository.UpsertAsync(networkPeerId, directSessionId, _activeIdentityContext.Identity!.SelfIdentityId);
                _logger.LogInformation("Upserted session with peer {PeerName} with session {SessionId}", remotePeer.Name, directSessionId);
            }
            
            var cryptoSessionId = new SessionId(directSessionId.Value);
            await _sessionManager.EstablishSessionAsInitiatorAsync(
                cryptoSessionId,
                remoteIdentityKey,
                remotePreKey,
                sharedSecret,
                ephemeralKey);
            _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", cryptoSessionId, remotePeer.Id);

            // Upsert PKH -> Peer mapping immediately after establishing session (initiator side)
            var establishedSpki = remoteIdentityKey.Value;
            var establishedPkh = SHA256.HashData(establishedSpki);
            await _pkhStore.ActivateIfChangedAsync(remotePeer.Id, establishedSpki, establishedPkh, DateTimeOffset.UtcNow, cancellationToken);

            // Build response payload and sign
            var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
            {
                EphemeralKey = ByteString.CopyFrom(ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
                SessionId = directSessionId.ToString()
            }.ToByteString();

            var signedPayloadBytes = _x3DhManager.SignPreKey(_activeIdentityContext.Keys.IdentitySigningKey, new PreKey(responsePayload.ToByteArray()));

            return new EstablishDirectSessionResult
            {
                SessionId = directSessionId.ToString(),
                ResponsePayloadBytes = responsePayload.ToByteArray(),
                IdentitySigningKeyBytes = _activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo(),
                PayloadSignatureBytes = signedPayloadBytes.Value
            };
        }
    }
}
