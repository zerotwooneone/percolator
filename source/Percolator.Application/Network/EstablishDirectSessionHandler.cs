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
using Percolator.Identity.Model;
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
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IX3DHManager _x3DhManager;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IPeerPublicSigningKeyStore _pkhStore;

        public EstablishDirectSessionHandler(
            ILogger<EstablishDirectSessionHandler> logger,
            ActiveIdentityContext activeIdentityContext,
            IX3DHOrchestrator x3dhOrchestrator,
            IDirectSessionManager sessionManager,
            IPeerIdentityRepository peerIdentityRepository,
            IPeerConnectionRepository peerConnectionRepository,
            IX3DHManager x3DhManager,
            IDirectSessionRepository directSessionRepository,
            IPeerPublicSigningKeyStore pkhStore)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _peerIdentityRepository = peerIdentityRepository;
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
            var remoteIdentityKey = new RatchetIdentityKey(request.RemoteIdentityKeyBytes);
            var requestPayloadSignature = new CryptoSignature(request.PayloadSignatureBytes);
            var requestPayload = new PreKey(request.SignedPayloadBytes);

            // Verify the signed pre-key payload
            if (!_x3DhManager.VerifySignature(remoteIdentityKey, requestPayload, requestPayloadSignature))
            {
                throw new CryptographicException("Invalid signature on signed pre-key.");
            }
            _logger.LogDebug("Signature verification successful");

            // Persist/update peer connection info based on endpoint
            var networkIdentitySigningKey = new DirectMessagePublicKey(request.RemoteIdentityKeyBytes);
            var timestamp = DateTimeOffset.Now;

            var existingPeerConnectionInfo = await _peerConnectionRepository.GetByPublicKey(networkIdentitySigningKey).ConfigureAwait(false);
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
            var remotePreKey = new RatchetEphemeralKey(request.RemoteEphemeral);
            var prekeyBundle = new X3dPreKeyBundle(
                remoteIdentityKey,
                remotePreKey,
                request.OneTimePreKeyBytes is not null ? new OneTimeKey(request.OneTimePreKeyBytes) : null);

            var sharedSecret = _x3dhOrchestrator.InitiateHandshake(prekeyBundle, ephemeralKey);
            _logger.LogInformation("X3DH handshake processed successfully as Initiator");

            var identityPeerId = new IdentityPeerId(peerConnectionInfo.Id.Value);
            var identity = await _peerIdentityRepository.GetByIdAsync(identityPeerId).ConfigureAwait(false);
            if (identity is null)
            {
                _logger.LogInformation("Peer with key hash {KeyHash} is unknown. Creating a new peer identity", Convert.ToBase64String(request.RemoteIdentityKeyBytes));
                var newPeerName = $"Peer-{Convert.ToBase64String(request.RemoteIdentityKeyBytes)}";
                identity = new PeerIdentity(identityPeerId);
                identity.SetDisplayName(new DisplayName(newPeerName));
                await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            }

            // Handshake-side identity mapping: bind PKH -> this peer id (idempotent if already bound to same peer)
            var initiatorSpki = request.RemoteIdentityKeyBytes;
            var initiatorPkh = SHA256.HashData(initiatorSpki);
            await _pkhStore.ActivateIfChangedAsync(identity.Id, initiatorSpki, initiatorPkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            // Now that the Peer exists, persist/update the PeerConnection
            await _peerConnectionRepository.SaveAsync(peerConnectionInfo).ConfigureAwait(false);

            var existingDirectSession =
                await _directSessionRepository.GetByRemotePeerIdAsync(networkPeerId,
                    _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false);
            var directSessionId = existingDirectSession?.SessionId 
                                  ?? new DirectSessionId(Guid.NewGuid());
            if (existingDirectSession is null)
            {
                await _directSessionRepository.UpsertAsync(networkPeerId, directSessionId, _activeIdentityContext.Identity!.SelfIdentityId).ConfigureAwait(false);
                _logger.LogInformation("Upserted session with peer {PeerName} with session {SessionId}", identity.DisplayName?.Value ?? identity.Id.Value.ToString(), directSessionId);
            }
            
            var cryptoSessionId = new SessionId(directSessionId.Value);
            var remoteEphemeral = request.OneTimePreKeyBytes is null
                ? new RatchetEphemeralKey(request.RemoteEphemeral)
                : new RatchetEphemeralKey(request.OneTimePreKeyBytes);
            await _sessionManager.EstablishSessionAsInitiatorAsync(
                cryptoSessionId,
                remoteIdentityKey,
                remoteEphemeral,
                sharedSecret,
                ephemeralKey).ConfigureAwait(false);
            _logger.LogInformation("Successfully established session {SessionId} with peer {PeerId}", cryptoSessionId, identity.Id);

            // Upsert PKH -> Peer mapping immediately after establishing session (initiator side)
            var establishedSpki = remoteIdentityKey.Value;
            var establishedPkh = SHA256.HashData(establishedSpki);
            await _pkhStore.ActivateIfChangedAsync(identity.Id, establishedSpki, establishedPkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            // Build responder payload carrying the allocated session id and optional inner envelope
            var responsePayload = new EstablishDirectSessionResponse.Types.ResponsePayload
            {
                SessionId = directSessionId.ToString()
            }.ToByteString();

            // Encrypt the response payload as an initial X3DH ratchet message for the initiator
            var ratchetMessage = await _sessionManager.EncryptMessageAsync(
                cryptoSessionId,
                new Plaintext(responsePayload.ToByteArray()))
                .ConfigureAwait(false);

            return new EstablishDirectSessionResult
            {
                SessionId = directSessionId.ToString(),
                ResponsePayloadBytes = responsePayload.ToByteArray(),
                IdentitySigningKeyBytes = _activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo(),
                RemoteEphemeralKeyBytes = ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo(),
                RatchetMessageBytes = ratchetMessage.Value
            };
        }
    }
}
