using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MediatR;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.Application.Network
{
    /// <summary>
    /// Used to create a pending session in the reverse signal flow
    /// </summary>
    public interface IEstablishDirectSessionService
    {
        Task<EstablishDirectSessionResult?> EstablishAsync(EstablishDirectSessionCommand request, CancellationToken cancellationToken);
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

        public EstablishDirectSessionService(
            ILogger<EstablishDirectSessionService> logger,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active,
            IPeerIdentityRepository peerIdentityRepository,
            IPeerRoutingProfileRepository peerRoutingProfileRepository,
            Percolator.Network.ISigningService signingService,
            IPendingSessionRepository pendingSessions,
            IClock clock,
            IMediator mediator)
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
