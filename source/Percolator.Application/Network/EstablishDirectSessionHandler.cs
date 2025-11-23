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
        private readonly ISecureMessagingService _secureMessaging;
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IPeerPublicSigningKeyStore _pkhStore;
        private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;

        public EstablishDirectSessionHandler(
            ILogger<EstablishDirectSessionHandler> logger,
            ActiveIdentityContext activeIdentityContext,
            ISecureMessagingService secureMessaging,
            IPeerIdentityRepository peerIdentityRepository,
            IDirectSessionRepository directSessionRepository,
            IPeerPublicSigningKeyStore pkhStore,
            IPeerRoutingProfileRepository peerRoutingProfileRepository)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _secureMessaging = secureMessaging;
            _peerIdentityRepository = peerIdentityRepository;
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

            // TODO: Verify signed pre-key payload via SessionCrypto in Step 8
            throw new NotSupportedException("Handshake verification cutover pending (Step 8): replace legacy VerifySignature");

            // Determine timestamps and build values
            var networkIdentitySigningKey = new DirectMessagePublicKey(request.RemoteIdentityKeyBytes);
            var timestamp = DateTimeOffset.Now;

            // TODO: Derive shared secret via IHandshakeService.InitiateStandardHandshake
            var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            throw new NotSupportedException("Handshake initiation cutover pending (Step 8): replace legacy InitiateHandshake");

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
            // TODO: Establish session via domain services
            throw new NotSupportedException("Direct session establish cutover pending (Step 8): replace legacy EstablishSessionAsInitiatorAsync");

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
            // TODO: Encrypt over new session once established
            var ratchetMessage = new SessionRatchetMessage(Array.Empty<byte>());

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
