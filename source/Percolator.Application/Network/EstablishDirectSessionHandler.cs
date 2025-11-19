using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
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
        private readonly ISecureMessagingService _secureMessaging;
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly IX3DHManager _x3DhManager;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IPeerPublicSigningKeyStore _pkhStore;
        private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;

        public EstablishDirectSessionHandler(
            ILogger<EstablishDirectSessionHandler> logger,
            ActiveIdentityContext activeIdentityContext,
            IX3DHOrchestrator x3dhOrchestrator,
            IDirectSessionManager sessionManager,
            ISecureMessagingService secureMessaging,
            IPeerIdentityRepository peerIdentityRepository,
            IX3DHManager x3DhManager,
            IDirectSessionRepository directSessionRepository,
            IPeerPublicSigningKeyStore pkhStore,
            IPeerRoutingProfileRepository peerRoutingProfileRepository)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _x3dhOrchestrator = x3dhOrchestrator;
            _sessionManager = sessionManager;
            _secureMessaging = secureMessaging;
            _peerIdentityRepository = peerIdentityRepository;
            _x3DhManager = x3DhManager;
            _directSessionRepository = directSessionRepository;
            _pkhStore = pkhStore;
            _peerRoutingProfileRepository = peerRoutingProfileRepository;
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

            // Determine timestamps and build values
            var networkIdentitySigningKey = new DirectMessagePublicKey(request.RemoteIdentityKeyBytes);
            var timestamp = DateTimeOffset.Now;

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

            // Identity domain owns PeerId minting. If identity not found, mint a new IdentityPeerId here (Identity domain responsibility) and create it.
            // Then, NetworkPeerId is derived from IdentityPeerId to keep ids aligned across domains.
            var identityPeerId = new IdentityPeerId(Guid.NewGuid());
            var identity = await _peerIdentityRepository.GetByIdAsync(identityPeerId).ConfigureAwait(false);
            if (identity is null)
            {
                _logger.LogInformation("Peer with key hash {KeyHash} is unknown. Creating a new peer identity", Convert.ToBase64String(request.RemoteIdentityKeyBytes));
                var pkhForName = SHA256.HashData(request.RemoteIdentityKeyBytes);
                var hex = Convert.ToHexString(pkhForName);
                var newPeerName = $"Peer-{hex.Substring(0, Math.Min(12, hex.Length))}";
                identity = new PeerIdentity(identityPeerId);
                identity.SetDisplayName(new DisplayName(newPeerName));
                await _peerIdentityRepository.SaveAsync(identity).ConfigureAwait(false);
            }
            // No legacy backfill needed: PeerConnections now FK to PeerIdentities

            // Handshake-side identity mapping: bind PKH -> this peer id (idempotent if already bound to same peer)
            var initiatorSpki = request.RemoteIdentityKeyBytes;
            var initiatorPkh = SHA256.HashData(initiatorSpki);
            await _pkhStore.ActivateIfChangedAsync(identity.Id, initiatorSpki, initiatorPkh, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

            // Mirror routing data into the Network domain's authoritative profile
            var networkPeerId = new NetworkPeerId(identity.Id.Value);
            var profile = await _peerRoutingProfileRepository.GetByIdAsync(networkPeerId, cancellationToken).ConfigureAwait(false)
                          ?? new PeerRoutingProfile();
            if (profile.Id is null)
            {
                profile.BindIdentity(networkPeerId);
            }
            profile.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(networkIdentitySigningKey.Value));
            profile.AddGrpcEndPoint(new GrpcEndPoint(request.PeerEndPoint, timestamp), timestamp);
            profile.RecordReachability(ReachabilityStatus.Online, timestamp);
            await _peerRoutingProfileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

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
            var ratchetMessage = await _secureMessaging.EncryptAsync(
                cryptoSessionId,
                new Plaintext(responsePayload.ToByteArray()))
                .ConfigureAwait(false);

            var result = new EstablishDirectSessionResult
            {
                SessionId = directSessionId.ToString(),
                ResponsePayloadBytes = responsePayload.ToByteArray(),
                IdentitySigningKeyBytes = _activeIdentityContext.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo(),
                RemoteEphemeralKeyBytes = ephemeralKey.PublicKey.ExportSubjectPublicKeyInfo(),
                RatchetMessageBytes = ratchetMessage.Value
            };

            // Record reachability as Online in the domain routing profile (if present)
            try
            {
                profile.RecordReachability(ReachabilityStatus.Online, DateTimeOffset.UtcNow);
                await _peerRoutingProfileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record reachability Online for {PeerId}", networkPeerId);
            }

            return result;
        }
    }
}
