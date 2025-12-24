using System;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MediatR;
using Percolator.Application.Identity;
using Percolator.Application.ReverseSignal;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Used to create a pending session in the reverse signal flow
    /// </summary>
    public interface IEstablishDirectSessionService
    {
        Task<EstablishDirectSessionResult?> EstablishAsync(EstablishDirectSessionCommand request, CancellationToken cancellationToken);

        Task QueueInviteAsync(HandshakeInitiatorHello initiatorHello, CancellationToken cancellationToken);
    }

    internal sealed class EstablishDirectSessionService : IEstablishDirectSessionService
    {
        private readonly ILogger<EstablishDirectSessionService> _logger;
        private readonly IActiveIdentityAccessor _activeIdentityAccessor;
        private readonly ActiveIdentityContext _active;
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly IPeerRoutingProfileRepository _peerRoutingProfileRepository;
        private readonly Percolator.Network.ISigningService _signingService;
        private readonly IPendingSessionRepository _pendingSessions;
        private readonly IClock _clock;
        private readonly IMediator _mediator;
        private readonly ICallbackEndpointValidator _callbackEndpointValidator;

        public EstablishDirectSessionService(
            ILogger<EstablishDirectSessionService> logger,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active,
            IPeerIdentityRepository peerIdentityRepository,
            IPeerRoutingProfileRepository peerRoutingProfileRepository,
            Percolator.Network.ISigningService signingService,
            IPendingSessionRepository pendingSessions,
            IClock clock,
            IMediator mediator,
            ICallbackEndpointValidator callbackEndpointValidator)
        {
            _logger = logger;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
            _peerIdentityRepository = peerIdentityRepository;
            _peerRoutingProfileRepository = peerRoutingProfileRepository;
            _signingService = signingService;
            _pendingSessions = pendingSessions;
            _clock = clock;
            _mediator = mediator;
            _callbackEndpointValidator = callbackEndpointValidator;
        }

        public async Task QueueInviteAsync(HandshakeInitiatorHello initiatorHello, CancellationToken cancellationToken)
        {
            if (initiatorHello is null) throw new ArgumentNullException(nameof(initiatorHello));

            if (!_activeIdentityAccessor.IsActive || _active.Identity is null || _active.Keys is null)
            {
                _logger.LogError("Local peer identity has not been established. Cannot accept reverse-signal invite");
                throw new InvalidOperationException("Server identity not initialized.");
            }

            if (!initiatorHello.HasInitiatorIdentityKeySpki)
            {
                throw new InvalidOperationException("Initiator identity key is required.");
            }

            // Resolve or create peer identity by PKH
            var initiatorSpki = initiatorHello.InitiatorIdentityKeySpki.ToByteArray();
            var initiatorPkh = SHA256.HashData(initiatorSpki);
            var identity = await _peerIdentityRepository.FindByPublicKeyHashAsync(initiatorPkh, cancellationToken).ConfigureAwait(false);
            if (identity is null)
            {
                var newId = Percolator.Identity.PeerId.NewId();
                var hex = Convert.ToHexString(initiatorPkh);
                identity = new PeerIdentity(newId);
                identity.SetDisplayName(new DisplayName($"Peer-{hex.Substring(0, Math.Min(12, hex.Length))}"));
                await _peerIdentityRepository.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            // Parse callback endpoint only from the (end-to-end protected) encrypted payload; do not derive from transport metadata.
            string? callbackHost = null;
            int? callbackPort = null;
            if (initiatorHello.HasEncryptedPayload && initiatorHello.EncryptedPayload.Length > 0)
            {
                var invitePayload = InviteHandshakeRequestPayload.Parser.ParseFrom(initiatorHello.EncryptedPayload);
                if (invitePayload.CallbackEndpoint is not null && invitePayload.CallbackEndpoint.HasHost && invitePayload.CallbackEndpoint.HasPort)
                {
                    callbackHost = invitePayload.CallbackEndpoint.Host;
                    callbackPort = (int)invitePayload.CallbackEndpoint.Port;

                    var validation = _callbackEndpointValidator.Validate(callbackHost, callbackPort.Value);
                    if (!validation.IsValid)
                    {
                        throw new InvalidOperationException(validation.ErrorMessage ?? "Callback endpoint is invalid.");
                    }
                }
            }

            var invitationBytes = initiatorHello.ToByteArray();
            var invitation = new HandshakeInvitation(invitationBytes);
            var inviterIdentityKey = new RatchetIdentityKey(initiatorSpki);
            var protocolVersion = initiatorHello.HasVersion ? new ProtocolVersion((int)initiatorHello.Version) : new ProtocolVersion(1);

            var pending = PendingSession.FromInvitationWithMetadata(
                PendingSessionId.NewId(),
                new PeerId(identity.Id.Value),
                protocolVersion,
                invitation,
                requestCorrelationId: null,
                isRelayed: false,
                inviterIdentityKey: inviterIdentityKey,
                callbackEndpointHost: callbackHost,
                callbackEndpointPort: callbackPort,
                _clock);

            await _pendingSessions.AddAsync(pending, cancellationToken).ConfigureAwait(false);
            await _mediator.Publish(new PendingSessionCreatedNotification(pending.Id), cancellationToken).ConfigureAwait(false);
        }

        public async Task<EstablishDirectSessionResult?> EstablishAsync(EstablishDirectSessionCommand request, CancellationToken cancellationToken)
        {
            if (!_activeIdentityAccessor.IsActive || _active.Identity is null || _active.Keys is null)
            {
                _logger.LogError("Local peer identity has not been established. Cannot respond to handshake");
                throw new InvalidOperationException("Server identity not initialized.");
            }

            // Verify the signed payload (ECDSA P-256 + SHA-256)
            var verified = _signingService.Verify(
                new Percolator.Network.Payload(request.SignedPayloadBytes),
                new Percolator.Network.Signature(request.PayloadSignatureBytes),
                new Percolator.Network.PublicKey(request.RemoteIdentityKeyBytes));
            if (!verified)
            {
                throw new CryptographicException("handshake payload signature invalid");
            }

            // Resolve or create peer identity by PKH
            var initiatorSpki = request.RemoteIdentityKeyBytes;
            var initiatorPkh = SHA256.HashData(initiatorSpki);
            var identity = await _peerIdentityRepository.FindByPublicKeyHashAsync(initiatorPkh, cancellationToken).ConfigureAwait(false);
            if (identity is null)
            {
                var newId = Percolator.Identity.PeerId.NewId();
                var hex = Convert.ToHexString(initiatorPkh);
                identity = new PeerIdentity(newId);
                identity.SetDisplayName(new DisplayName($"Peer-{hex.Substring(0, Math.Min(12, hex.Length))}"));
                await _peerIdentityRepository.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            // Mirror routing data into Network profile
            var networkPeerId = new Percolator.Network.PeerId(identity.Id.Value);
            var profile = await _peerRoutingProfileRepository.GetByIdAsync(networkPeerId, cancellationToken).ConfigureAwait(false)
                          ?? new PeerRoutingProfile();
            if (profile.Id is null)
            {
                profile.BindIdentity(networkPeerId);
            }
            profile.SetIdentityPublicKey(new Percolator.Network.ValueObjects.IdentityPublicKey(initiatorSpki));
            profile.AddGrpcEndPoint(new GrpcEndPoint(request.PeerEndPoint, _clock.UtcNow), _clock.UtcNow);
            profile.RecordReachability(ReachabilityStatus.Online, _clock.UtcNow);
            await _peerRoutingProfileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);

            // Enqueue a pending session (no ratchet session created here)
            var invitation = new HandshakeInvitation(request.SignedPayloadBytes);
            var pending = PendingSession.FromInvitation(
                PendingSessionId.NewId(),
                new Percolator.Cryptography.Primitives.PeerId(identity.Id.Value),
                new ProtocolVersion(1),
                invitation,
                _clock);
            await _pendingSessions.AddAsync(pending, cancellationToken).ConfigureAwait(false);
            await _mediator.Publish(new PendingSessionCreatedNotification(pending.Id), cancellationToken).ConfigureAwait(false);

            // todo: implement a strategy to automatically accept the request
            return null;
        }
    }
}
