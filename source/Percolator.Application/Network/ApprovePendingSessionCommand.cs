using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.ReverseSignal;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Network;
using Percolator.Network.ValueObjects;

namespace Percolator.Application.Network
{
    public abstract record ApprovePendingSessionResult
    {
        public sealed record Accepted(string SendPath, RequestCorrelationId RequestCorrelationId) : ApprovePendingSessionResult;
        public sealed record RejectedNotReady : ApprovePendingSessionResult;
        public sealed record RejectedInvalid : ApprovePendingSessionResult;
        public sealed record RejectedExpired : ApprovePendingSessionResult;
        public sealed record Failed(string ErrorMessage) : ApprovePendingSessionResult;
    }

    public sealed record ApprovePendingSessionCommand(PendingSessionId PendingSessionId) : IRequest<ApprovePendingSessionResult>;

    internal sealed class ApprovePendingSessionHandler : IRequestHandler<ApprovePendingSessionCommand, ApprovePendingSessionResult>
    {
        private readonly ILogger<ApprovePendingSessionHandler> _logger;
        private readonly IActiveIdentityAccessor _activeIdentityAccessor;
        private readonly ActiveIdentityContext _active;
        private readonly IPendingSessionRepository _pending;
        private readonly IClock _clock;
        private readonly ICallbackEndpointValidator _callbackEndpointValidator;
        private readonly ISessionCrypto _sessionCrypto;
        private readonly IHandshakePlanner _handshakePlanner;
        private readonly ISessionRepository _sessions;
        private readonly IPeerRoutingProfileRepository _profileRepository;
        private readonly IInviteHandshakeResponseDeliveryService _delivery;

        public ApprovePendingSessionHandler(
            ILogger<ApprovePendingSessionHandler> logger,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active,
            IPendingSessionRepository pending,
            IClock clock,
            ICallbackEndpointValidator callbackEndpointValidator,
            ISessionCrypto sessionCrypto,
            IHandshakePlanner handshakePlanner,
            ISessionRepository sessions,
            IPeerRoutingProfileRepository profileRepository,
            IInviteHandshakeResponseDeliveryService delivery)
        {
            _logger = logger;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
            _pending = pending;
            _clock = clock;
            _callbackEndpointValidator = callbackEndpointValidator;
            _sessionCrypto = sessionCrypto;
            _handshakePlanner = handshakePlanner;
            _sessions = sessions;
            _profileRepository = profileRepository;
            _delivery = delivery;
        }

        public async Task<ApprovePendingSessionResult> Handle(ApprovePendingSessionCommand request, CancellationToken cancellationToken)
        {
            if (!_activeIdentityAccessor.IsActive || _active.Identity is null || _active.Keys is null)
            {
                return new ApprovePendingSessionResult.RejectedNotReady();
            }

            var pending = await _pending.GetAsync(request.PendingSessionId, cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }

            if (pending.ExpiresAtUtc is not null && pending.ExpiresAtUtc.Value <= _clock.UtcNow)
            {
                await _pending.DeleteAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                return new ApprovePendingSessionResult.RejectedExpired();
            }

            if (pending.RequestCorrelationId is null)
            {
                throw new InvalidOperationException("PendingSession missing request_correlation_id. Purge outdated pending sessions.");
            }

            // Parse stored invitation blob as EstablishDirectSessionRequest (envelope)
            EstablishDirectSessionRequest invitationEnvelope;
            try
            {
                invitationEnvelope = EstablishDirectSessionRequest.Parser.ParseFrom(pending.Invitation.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pending session {PendingId} invitation bytes were not a valid EstablishDirectSessionRequest", pending.Id.Value);
                return new ApprovePendingSessionResult.RejectedInvalid();
            }

            if (!invitationEnvelope.HasInviterIdentityKey || invitationEnvelope.InviterIdentityKey.Length == 0)
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }
            if (!invitationEnvelope.HasPayload || invitationEnvelope.Payload.Length == 0)
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }

            // Parse InviteHandshakeRequestPayload
            InviteHandshakeRequestPayload payload;
            try
            {
                payload = InviteHandshakeRequestPayload.Parser.ParseFrom(invitationEnvelope.Payload);
            }
            catch
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }

            DnsEndPoint? directCallbackEndpoint = null;
            if (!pending.IsRelayed)
            {
                if (string.IsNullOrWhiteSpace(pending.CallbackEndpointHost) || pending.CallbackEndpointPort is null)
                {
                    return new ApprovePendingSessionResult.RejectedInvalid();
                }

                // Re-validate callback endpoint boundary before mutating routing profile.
                var validation = _callbackEndpointValidator.Validate(pending.CallbackEndpointHost, pending.CallbackEndpointPort.Value);
                if (!validation.IsValid)
                {
                    return new ApprovePendingSessionResult.RejectedInvalid();
                }

                directCallbackEndpoint = new DnsEndPoint(pending.CallbackEndpointHost, pending.CallbackEndpointPort.Value);
            }

            if (payload.InviterPreKey is null)
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }
            if (!payload.InviterPreKey.HasInviterSignedPreKey || payload.InviterPreKey.InviterSignedPreKey.Length == 0)
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }
            if (!payload.InviterPreKey.HasPreKeySignature || payload.InviterPreKey.PreKeySignature.Length == 0)
            {
                return new ApprovePendingSessionResult.RejectedInvalid();
            }

            // Pre-key IDs are inviter-local and are not transmitted.
            // The acceptor must complete X3DH using only public key material + signatures.
            var signedPreKeyId = Guid.Empty;
            Guid? oneTimePreKeyId = null;
            OneTimeKey? oneTimePreKey = null;
            if (payload.InviterPreKey.HasInviterOneTimePreKey && payload.InviterPreKey.InviterOneTimePreKey.Length > 0)
            {
                oneTimePreKey = new OneTimeKey(payload.InviterPreKey.InviterOneTimePreKey.ToByteArray());
            }

            var inviterIdentityKeySpki = invitationEnvelope.InviterIdentityKey.ToByteArray();
            var inviterBundle = new Percolator.Cryptography.PreKeyBundle(
                new RatchetIdentityKey(inviterIdentityKeySpki),
                signedPreKeyId,
                new PreKey(payload.InviterPreKey.InviterSignedPreKey.ToByteArray()),
                new Percolator.Cryptography.Signature(payload.InviterPreKey.PreKeySignature.ToByteArray()),
                oneTimePreKeyId,
                oneTimePreKey,
                payload.ExpiresAtUtc?.ToDateTimeOffset());

            // Validate bundle before deriving shared secret.
            _handshakePlanner.ValidatePreKeyBundle(inviterBundle);

            var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
            var x3 = _sessionCrypto.X3DH_Initiate(localIkPriv, inviterBundle);

            // Create initiator session (acceptor side) with a session id chosen by the acceptor.
            var sessionId = SessionId.NewId();
            var root = new RootKey(x3.SharedSecret.Value);
            var proto = pending.ProtocolVersion;
            var session = RatchetBootstrap.CreateInitiatorSession(
                sessionId,
                new Percolator.Cryptography.Primitives.PeerId(pending.RemotePeerId.Value),
                proto,
                root,
                _clock);
            await _sessions.AddAsync(session, cancellationToken).ConfigureAwait(false);

            var inner = new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = sessionId.Value.ToString()
            };
            var initial = session.Encrypt(new Plaintext(inner.ToByteArray()), _clock);

            var inviterNetPeerId = new Percolator.Network.PeerId(pending.RemotePeerId.Value);
            if (!pending.IsRelayed)
            {
                // Routing-profile mutation boundary: only on explicit acceptance of a direct invite.
                var profile = await _profileRepository.GetByIdAsync(inviterNetPeerId, cancellationToken).ConfigureAwait(false)
                    ?? new PeerRoutingProfile();
                if (profile.Id is null)
                {
                    profile.BindIdentity(inviterNetPeerId);
                }
                profile.AddGrpcEndPoint(
                    new GrpcEndPoint(directCallbackEndpoint!, _clock.UtcNow),
                    _clock.UtcNow);
                profile.SetIdentityPublicKey(new IdentityPublicKey(inviterIdentityKeySpki));
                await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            }

            // Send InviteHandshakeResponse via the new RPC.
            var response = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = pending.RequestCorrelationId.ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.Value),
                InitialRatchetMessage = ByteString.CopyFrom(initial.Value)
            };


            var delivery = await _delivery.DeliverAsync(inviterNetPeerId, directCallbackEndpoint, response, cancellationToken).ConfigureAwait(false);
            if (!delivery.Success)
            {
                _logger.LogWarning(delivery.Error, "Failed to send InviteHandshakeResponse for pending session {PendingId}", pending.Id.Value);
                return new ApprovePendingSessionResult.Failed(delivery.Error?.Message ?? "Send failed");
            }

            await _pending.DeleteAsync(pending.Id, cancellationToken).ConfigureAwait(false);
            return new ApprovePendingSessionResult.Accepted(delivery.SendPath, pending.RequestCorrelationId.Value);
        }
    }
}
