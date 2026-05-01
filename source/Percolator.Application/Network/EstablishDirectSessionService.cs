using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using MediatR;
using Percolator.Application.ReverseSignal;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Application.Network
{
    internal sealed class EstablishDirectSessionService : IEstablishDirectSessionService
    {
        private readonly ILogger<EstablishDirectSessionService> _logger;
        private readonly ISelfIdentityKeysStore _keysStore;
        private readonly IPeerIdentityRepository _peerIdentityRepository;
        private readonly Percolator.Network.ISigningService _signingService;
        private readonly IPendingSessionRepository _pendingSessions;
        private readonly IClock _clock;
        private readonly IMediator _mediator;
        private readonly ICallbackEndpointValidator _callbackEndpointValidator;

        public EstablishDirectSessionService(
            ILogger<EstablishDirectSessionService> logger,
            ISelfIdentityKeysStore keysStore,
            IPeerIdentityRepository peerIdentityRepository,
            Percolator.Network.ISigningService signingService,
            IPendingSessionRepository pendingSessions,
            IClock clock,
            IMediator mediator,
            ICallbackEndpointValidator callbackEndpointValidator)
        {
            _logger = logger;
            _keysStore = keysStore;
            _peerIdentityRepository = peerIdentityRepository;
            _signingService = signingService;
            _pendingSessions = pendingSessions;
            _clock = clock;
            _mediator = mediator;
            _callbackEndpointValidator = callbackEndpointValidator;
        }

        public async Task<RequestCorrelationId> QueueInviteAsync(
            SelfId selfIdentityId,
            byte[] inviterIdentityKeySpki,
            byte[] payloadBytes,
            byte[] payloadSignatureBytes,
            bool isRelayed,
            Percolator.Identity.PeerId? relayHostPeerId,
            CancellationToken cancellationToken)
        {
            if (inviterIdentityKeySpki is null || inviterIdentityKeySpki.Length == 0)
            {
                throw new ArgumentException("inviter identity key required", nameof(inviterIdentityKeySpki));
            }

            if (payloadBytes is null || payloadBytes.Length == 0)
            {
                throw new ArgumentException("payload required", nameof(payloadBytes));
            }

            if (payloadSignatureBytes is null || payloadSignatureBytes.Length == 0)
            {
                throw new ArgumentException("payload signature required", nameof(payloadSignatureBytes));
            }

            var keys = await _keysStore.LoadAsync(selfIdentityId, cancellationToken).ConfigureAwait(false);
            if (keys is null)
            {
                throw new InvalidOperationException("Server identity not initialized.");
            }

            var payloadVerified = _signingService.Verify(
                Percolator.Network.Payload.FromBytes(payloadBytes),
                Percolator.Network.Signature.FromBytes(payloadSignatureBytes),
                Percolator.Network.PublicKey.FromBytes(inviterIdentityKeySpki));
            if (!payloadVerified)
            {
                throw new InvalidOperationException("handshake payload signature invalid");
            }

            InviteHandshakeRequestPayload payload;
            try
            {
                payload = InviteHandshakeRequestPayload.Parser.ParseFrom(payloadBytes);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message);
            }

            if (!payload.HasRequestCorrelationId || string.IsNullOrWhiteSpace(payload.RequestCorrelationId))
            {
                throw new InvalidOperationException("request_correlation_id is required.");
            }

            RequestCorrelationId requestCorrelationId;
            try
            {
                if (!Guid.TryParse(payload.RequestCorrelationId, out var correlationGuid) || correlationGuid == Guid.Empty)
                {
                    throw new InvalidOperationException("request_correlation_id must be a non-empty GUID.");
                }

                requestCorrelationId = new RequestCorrelationId(correlationGuid);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message);
            }

            if (payload.ExpiresAtUtc is null)
            {
                throw new InvalidOperationException("expires_at_utc is required.");
            }

            var expiresAtUtc = payload.ExpiresAtUtc.ToDateTimeOffset();
            if (_clock.UtcNow >= expiresAtUtc)
            {
                throw new InvalidOperationException("invite is expired.");
            }

            // Replay/DoS: reject duplicates until expiry
            await foreach (var existing in _pendingSessions.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (existing.RequestCorrelationId == requestCorrelationId)
                {
                    if (!existing.IsExpiredAt(_clock.UtcNow))
                    {
                        throw new InvalidOperationException("duplicate request_correlation_id");
                    }
                }
            }

            if (!payload.HasInviterHost || string.IsNullOrWhiteSpace(payload.InviterHost))
            {
                throw new InvalidOperationException("inviter_host is required.");
            }

            if (!payload.HasInviterPort)
            {
                throw new InvalidOperationException("inviter_port is required.");
            }

            string? callbackHost = null;
            int? callbackPort = null;
            if (!isRelayed)
            {
                var validation = _callbackEndpointValidator.Validate(payload.InviterHost, (int)payload.InviterPort);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(validation.ErrorMessage ?? "Callback endpoint is invalid.");
                }

                callbackHost = payload.InviterHost;
                callbackPort = (int)payload.InviterPort;
            }

            if (payload.InviterPreKey is null)
            {
                throw new InvalidOperationException("inviter_pre_key is required.");
            }

            if (!payload.InviterPreKey.HasInviterSignedPreKey || payload.InviterPreKey.InviterSignedPreKey.Length == 0)
            {
                throw new InvalidOperationException("inviter_signed_pre_key is required.");
            }

            if (!payload.InviterPreKey.HasPreKeySignature || payload.InviterPreKey.PreKeySignature.Length == 0)
            {
                throw new InvalidOperationException("pre_key_signature is required.");
            }

            var preKeyVerified = _signingService.Verify(
                Percolator.Network.Payload.FromBytes(payload.InviterPreKey.InviterSignedPreKey.ToByteArray()),
                Percolator.Network.Signature.FromBytes(payload.InviterPreKey.PreKeySignature.ToByteArray()),
                Percolator.Network.PublicKey.FromBytes(inviterIdentityKeySpki));
            if (!preKeyVerified)
            {
                throw new InvalidOperationException("pre_key_signature invalid");
            }

            // Resolve or create peer identity by PKH
            var initiatorPkh = SHA256.HashData(inviterIdentityKeySpki);
            var identity = await _peerIdentityRepository.FindByPublicKeyHashAsync(initiatorPkh, cancellationToken).ConfigureAwait(false);
            if (identity is null)
            {
                var newId = Percolator.Identity.PeerId.NewId();
                var hex = Convert.ToHexString(initiatorPkh);
                identity = new PeerIdentity(newId);
                identity.SetDisplayName(new DisplayName($"Peer-{hex.Substring(0, Math.Min(12, hex.Length))}"));
                var now = _clock.UtcNow;
                identity.AddKey(inviterIdentityKeySpki, notBefore: now, expiresAt: now.AddYears(100), now: now);
                await _peerIdentityRepository.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            }

            // Do not upsert routing profile on invite receipt (TOFU boundary enforced)

            var invitationEnvelope = new EstablishDirectSessionRequest
            {
                Version = 1,
                InviterIdentityKey = ByteString.CopyFrom(inviterIdentityKeySpki),
                Payload = ByteString.CopyFrom(payloadBytes),
                PayloadSignature = ByteString.CopyFrom(payloadSignatureBytes)
            };
            var invitation = HandshakeInvitation.FromBytes(invitationEnvelope.ToByteArray());
            var inviterIdentityKey = RatchetIdentityKey.FromBytes(inviterIdentityKeySpki);
            var protocolVersion = payload.HasVersion ? new ProtocolVersion((int)payload.Version) : new ProtocolVersion(1);

            var pending = PendingSession.FromInvitationWithMetadata(
                PendingSessionId.NewId(),
                new PeerId(identity.Id.Value),
                protocolVersion,
                invitation,
                requestCorrelationId: requestCorrelationId,
                isRelayed: isRelayed,
                relayHostPeerId: relayHostPeerId is null ? null : new Percolator.Cryptography.Primitives.PeerId(relayHostPeerId.Value),
                inviterIdentityKey: inviterIdentityKey,
                callbackEndpointHost: callbackHost,
                callbackEndpointPort: callbackPort,
                _clock,
                expiresAtUtc: expiresAtUtc);

            await _pendingSessions.AddAsync(pending, cancellationToken).ConfigureAwait(false);
            await _mediator.Publish(new PendingSessionCreatedNotification(pending.Id), cancellationToken).ConfigureAwait(false);

            return requestCorrelationId;
        }
    }
}
