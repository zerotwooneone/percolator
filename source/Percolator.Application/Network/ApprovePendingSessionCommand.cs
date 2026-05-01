using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.ReverseSignal;
using Percolator.Application.Services;
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
        private readonly IDirectSessionLocator _directSessions;
        private readonly IDirectSessionMappingWriter _directSessionMappingWriter;
        private readonly ISecureMessagingService _secureMessaging;
        private readonly IMessageTransportService _transport;
        private readonly IMediator _mediator;

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
            IInviteHandshakeResponseDeliveryService delivery,
            IDirectSessionLocator directSessions,
            IDirectSessionMappingWriter directSessionMappingWriter,
            ISecureMessagingService secureMessaging,
            IMessageTransportService transport,
            IMediator mediator)
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
            _directSessions = directSessions;
            _directSessionMappingWriter = directSessionMappingWriter;
            _secureMessaging = secureMessaging;
            _transport = transport;
            _mediator = mediator;
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
                var correlationId = pending.RequestCorrelationId;
                await _pending.DeleteAsync(pending.Id, cancellationToken).ConfigureAwait(false);
                await _mediator.Publish(
                        new PendingSessionRemovedNotification(pending.Id, correlationId, PendingSessionRemoveReason.Expired),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ApprovePendingSessionResult.RejectedExpired();
            }

            // Parse stored invitation blob as EstablishDirectSessionRequest (envelope)
            EstablishDirectSessionRequest invitationEnvelope;
            try
            {
                invitationEnvelope = EstablishDirectSessionRequest.Parser.ParseFrom(pending.Invitation.ToArray());
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
                oneTimePreKey = OneTimeKey.FromBytes(payload.InviterPreKey.InviterOneTimePreKey.ToByteArray());
            }

            var inviterIdentityKeySpki = invitationEnvelope.InviterIdentityKey.ToByteArray();
            var inviterBundle = new Percolator.Cryptography.PreKeyBundle(
                RatchetIdentityKey.FromBytes(inviterIdentityKeySpki),
                signedPreKeyId,
                PreKey.FromBytes(payload.InviterPreKey.InviterSignedPreKey.ToByteArray()),
                Percolator.Cryptography.Signature.FromBytes(payload.InviterPreKey.PreKeySignature.ToByteArray()),
                oneTimePreKeyId,
                oneTimePreKey,
                payload.ExpiresAtUtc?.ToDateTimeOffset());

            // Validate bundle before deriving shared secret.
            _handshakePlanner.ValidatePreKeyBundle(inviterBundle);

            var localIkPriv = PrivatePreKey.FromBytesOwned(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
            var x3 = _sessionCrypto.X3DH_Initiate(localIkPriv, inviterBundle);

            // Create initiator session (acceptor side) with a session id chosen by the acceptor.
            var sessionId = SessionId.NewId();
            var root = RootKey.FromBytesOwned(x3.SharedSecret.ToArray());
            var proto = pending.ProtocolVersion;
            var session = RatchetBootstrap.CreateInitiatorSession(
                sessionId,
                new Percolator.Cryptography.Primitives.PeerId(pending.RemotePeerId.Value),
                proto,
                root,
                _clock);
            await _sessions.AddAsync(session, cancellationToken).ConfigureAwait(false);

            // Persist DirectSession mapping for conversation lookup
            var inviterNetPeerId = new Percolator.Network.PeerId(pending.RemotePeerId.Value);
            var directSessionId = new DirectSessionId(sessionId.Value);
            await _directSessionMappingWriter.WriteMappingAsync(
                inviterNetPeerId,
                directSessionId,
                _active.Identity.SelfIdentityId.Value,
                cancellationToken).ConfigureAwait(false);

            await _mediator.Publish(
                    new SecureSessionCreatedNotification(
                        sessionId,
                        SecureSessionCreatedReason.AcceptedInvite,
                        new Percolator.Cryptography.Primitives.PeerId(pending.RemotePeerId.Value),
                        proto),
                    cancellationToken)
                .ConfigureAwait(false);

            var inner = new ResponderInnerHello
            {
                Version = 1,
                DirectSessionId = sessionId.Value.ToString()
            };
            var initial = session.Encrypt(Plaintext.FromBytes(inner.ToByteArray()), _clock);

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
                profile.SetIdentityPublicKey(IdentityPublicKey.FromBytes(inviterIdentityKeySpki));
                await _profileRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            }

            // Send InviteHandshakeResponse via the new RPC.
            var response = new InviteHandshakeResponse
            {
                Version = 1,
                RequestCorrelationId = pending.RequestCorrelationId.ToString(),
                AcceptorIdentityKey = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                AcceptorX3DhEphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.ToArray()),
                InitialRatchetMessage = ByteString.CopyFrom(initial.ToArray())
            };

            InviteHandshakeResponseDeliveryResult delivery;
            if (pending.IsRelayed)
            {
                if (pending.RelayHostPeerId is null)
                {
                    return new ApprovePendingSessionResult.Failed("Relayed pending session is missing relay host metadata");
                }

                try
                {
                    await SendInviteHandshakeResponseViaRelayHostAsync(
                            pending,
                            inviterIdentityKeySpki,
                            response,
                            cancellationToken)
                        .ConfigureAwait(false);
                    delivery = new InviteHandshakeResponseDeliveryResult(true, $"Relay:{pending.RelayHostPeerId.Value}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deliver InviteHandshakeResponse via relay host {RelayHostPeerId}", pending.RelayHostPeerId.Value);
                    return new ApprovePendingSessionResult.Failed(ex.Message);
                }
            }
            else
            {
                delivery = await _delivery.DeliverAsync(inviterNetPeerId, directCallbackEndpoint, response, cancellationToken).ConfigureAwait(false);
                if (!delivery.Success)
                {
                    _logger.LogWarning(delivery.Error, "Failed to send InviteHandshakeResponse for pending session {PendingId}", pending.Id.Value);
                    return new ApprovePendingSessionResult.Failed(delivery.Error?.Message ?? "Send failed");
                }
            }

            var acceptedCorrelationId = pending.RequestCorrelationId;
            await _pending.DeleteAsync(pending.Id, cancellationToken).ConfigureAwait(false);
            await _mediator.Publish(
                    new PendingSessionRemovedNotification(pending.Id, acceptedCorrelationId, PendingSessionRemoveReason.Accepted),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ApprovePendingSessionResult.Accepted(delivery.SendPath, pending.RequestCorrelationId);
        }

        private async Task SendInviteHandshakeResponseViaRelayHostAsync(
            PendingSession pending,
            byte[] inviterIdentityKeySpki,
            InviteHandshakeResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.RelayHostPeerId is null) throw new InvalidOperationException("RelayHostPeerId is required for relayed pending sessions");
            if (inviterIdentityKeySpki is null || inviterIdentityKeySpki.Length == 0) throw new InvalidOperationException("Inviter identity key SPKI is required");

            var relayHostPeerId = new Percolator.Identity.PeerId(pending.RelayHostPeerId.Value);

            var relaySessionId = await _directSessions.GetAsync(relayHostPeerId, _active.Identity!.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
            if (relaySessionId is null)
            {
                throw new InvalidOperationException("No relay host session available");
            }

            var inviterPkh = SHA256.HashData(inviterIdentityKeySpki);
            var mqReq = new EnqueueOpaqueMessageRequest
            {
                Version = 1,
                RecipientPublicKeyHash = ByteString.CopyFrom(inviterPkh),
                MessageBlob = ByteString.CopyFrom(response.ToByteArray())
            };

            var env = new InternalEnvelope
            {
                MessageQueueEnvelope = new MessageQueueEnvelope
                {
                    Version = 1,
                    EnqueueOpaqueMessageRequest = mqReq
                }
            };

            var plain = Plaintext.FromBytes(env.ToByteArray());
            var sid = new Percolator.Cryptography.SessionId(relaySessionId.Value.Value);
            var cipher = await _secureMessaging.EncryptAsync(sid, plain, cancellationToken).ConfigureAwait(false);

            var _ = await _transport.SendMessageAsync(relayHostPeerId, relaySessionId.Value, cipher, cancellationToken).ConfigureAwait(false);
        }
    }
}
