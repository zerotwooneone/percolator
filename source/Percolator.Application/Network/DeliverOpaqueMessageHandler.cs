using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;
using Percolator.Prekey.Handlers;
using Percolator.Application.Network.Handshake;
using Percolator.Identity;
using NetworkPeerId = Percolator.Network.PeerId;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
    {
        private readonly ILogger<DeliverOpaqueMessageHandler> _logger;
        private readonly IMediator _mediator;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly IRatchetKeyIndex _ratchetLookup;
        private readonly RelayOrchestrator _relayOrchestrator;
        private readonly IPeerRoutingProfileRepository _profileRepository;
        private readonly IProfileRoutePlanner _routePlanner;
        private readonly ISecureMessagingService _secureMessaging;
        // Centralized allowlist to avoid drift with documentation and tests.
        private static readonly HashSet<InternalEnvelope.ApplicationPayloadOneofCase> AllowedCases = new()
        {
            InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.FileShareEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.SubmitPreKeyBundleResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.EnqueueOpaqueMessageResponse
        };

        public DeliverOpaqueMessageHandler(
            ILogger<DeliverOpaqueMessageHandler> logger,
            IMediator mediator,
            IDirectSessionRepository directSessionRepository,
            IRatchetKeyIndex ratchetLookup,
            RelayOrchestrator relayOrchestrator,
            IPeerRoutingProfileRepository profileRepository,
            IProfileRoutePlanner routePlanner,
            ISecureMessagingService secureMessaging)
        {
            _logger = logger;
            _mediator = mediator;
            _directSessionRepository = directSessionRepository;
            _ratchetLookup = ratchetLookup;
            _relayOrchestrator = relayOrchestrator;
            _profileRepository = profileRepository;
            _routePlanner = routePlanner;
            _secureMessaging = secureMessaging;
        }

        private async Task<InternalEnvelope?> HandlePrekeyEnvelopeAsync(PrekeyEnvelope prekeyEnvelope,
            NetworkPeerId remotePeerId, CancellationToken ct)
        {
            switch (prekeyEnvelope.MessageCase)
            {
                case PrekeyEnvelope.MessageOneofCase.SubmitPreKeyBundleRequest:
                    var upload = prekeyEnvelope.SubmitPreKeyBundleRequest;
                    if (!upload.HasIdentityKey) throw new InvalidOperationException("Identity key is required");
                    if (!upload.HasSignedPreKeyId) throw new InvalidOperationException("Signed pre-key ID is required");
                    if (!upload.HasSignedPreKey) throw new InvalidOperationException("Signed pre-key is required");
                    if (!upload.HasPreKeySignature) throw new InvalidOperationException("Pre-key signature is required");
                    if (upload.OneTimePreKeys.Count == 0) throw new InvalidOperationException("At least one one-time pre-key is required");
                    const int maxBundles = 100;
                    if (upload.OneTimePreKeys.Count > maxBundles) throw new InvalidOperationException($"Too many one-time pre-keys. Maximum is {maxBundles}");
                    foreach (var oneTimePreKey in upload.OneTimePreKeys)
                    {
                        if (!oneTimePreKey.HasId) throw new InvalidOperationException("One-time pre-key is required");
                        if (!oneTimePreKey.HasPublicKey) throw new InvalidOperationException("One-time pre-key public key is required");
                    }
                    if (upload.ExpiresUtc.ToDateTimeOffset() < DateTimeOffset.Now) throw new InvalidOperationException("Pre-key bundle has expired");

                    var cmd = new SubmitPreKeyBundleCommand
                    {
                        PublicSigningKey = upload.IdentityKey.ToByteArray(),
                        SignedPreKeyId = new Guid(upload.SignedPreKeyId.Span),
                        SignedPreKey = upload.SignedPreKey.ToByteArray(),
                        PreKeySignature = upload.PreKeySignature.ToByteArray(),
                        OneTimePreKeys = upload.OneTimePreKeys.Select(x => new SubmitPreKeyBundleCommand.OneTimePreKey(
                            new Guid(x.Id.Span), x.PublicKey.ToByteArray())).ToList(),
                        Expires = upload.ExpiresUtc.ToDateTimeOffset(),
                        RemotePeerId = remotePeerId
                    };
                    await _mediator.Send(cmd, ct).ConfigureAwait(false);
                    return new InternalEnvelope { SubmitPreKeyBundleResponse = new SubmitPreKeyBundleResponse { Version = 1 } };
                case PrekeyEnvelope.MessageOneofCase.GetPreKeyBundleRequest:
                    var getReq = prekeyEnvelope.GetPreKeyBundleRequest;
                    if (!getReq.HasPublicKeyHash) throw new InvalidOperationException("PublicKeyHash is required");
                    var bundle = await _mediator.Send(new Percolator.Prekey.Handlers.GetPreKeyBundleQuery(
                        IdentityPublicKeyHash.FromSpan(getReq.PublicKeyHash.Span)), ct).ConfigureAwait(false);
                    var resp = new GetPreKeyBundleResponse { Version = 1 };
                    if (bundle is not null)
                    {
                        var msg = new GetPreKeyBundleResponse.Types.PreKeyBundle
                        {
                            Version = 1,
                            IdentityKey = ByteString.CopyFrom(bundle.IdentitySigningKey.Span),
                            SignedPreKeyId = ByteString.CopyFrom(bundle.SignedPreKeyId.ToByteArray()),
                            SignedPreKey = ByteString.CopyFrom(bundle.SignedPreKey.Span),
                            PreKeySignature = ByteString.CopyFrom(bundle.SignedPreKeySignature.Span)
                        };
                        if (bundle.OneTimePreKey is not null)
                        {
                            msg.OneTimeKeys.Add(new GetPreKeyBundleResponse.Types.OneTimeKey
                            {
                                Version = 1,
                                OneTimeKeyId = ByteString.CopyFrom(bundle.OneTimePreKeyId!.Value.ToByteArray()),
                                KeyBytes = ByteString.CopyFrom(bundle.OneTimePreKey.Span)
                            });
                        }
                        resp.PreKeyBundle = msg;
                    }
                    return new InternalEnvelope { GetPreKeyBundleResponse = resp };
                default:
                    _logger.LogWarning("Received unhandled prekey message type: {MessageType}", prekeyEnvelope.MessageCase);
                    return null;
            }
        }

        public async Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
        {
            var selfIdentityId = request.SelfIdentityId.Value;
            _logger.LogInformation("Processing opaque message (session inferred from ratchet header)");
            
                var sessionRatchetMessage = SessionRatchetMessage.FromBytes(request.PayloadBytes);
                var header = sessionRatchetMessage.GetHeader();
                var ratchetKey = header.PreKey;
                var resolved = await _secureMessaging.DecryptInboundAsync(new CryptoSelfId(selfIdentityId), sessionRatchetMessage, cancellationToken).ConfigureAwait(false);
                if (resolved is null)
                {
                    _logger.LogWarning("Decrypt returned null; returning empty result without side-effects");
                    return new DeliverOpaqueMessageResult();
                }
                var inferredSessionId = resolved.Value.sessionId;
                var plaintext = resolved.Value.plaintext;
                var nonNullDirectSessionId = new DirectSessionId(inferredSessionId.Value);
                if (plaintext is null)
                {
                    _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", inferredSessionId);
                    return new DeliverOpaqueMessageResult();
                }

                await _ratchetLookup.UpsertAsync(new CryptoSelfId(selfIdentityId), inferredSessionId, ratchetKey, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

                var directSession = await _directSessionRepository.GetBySessionIdAsync(nonNullDirectSessionId, new NetworkSelfId(selfIdentityId)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"No direct session mapping found for session {inferredSessionId}");
                var remotePeerId = directSession.RemotePeerId;
                _logger.LogInformation("Resolved remote peer {PeerId} for session {SessionId}", remotePeerId, directSession.SessionId);
                
                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Span);
                if (internalEnvelope.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.None)
                {
                    _logger.LogWarning("Received unhandled one-of message type: {MessageType}", internalEnvelope.ApplicationPayloadCase);
                    return new DeliverOpaqueMessageResult();
                }

                if (!internalEnvelope.HasSourceDeviceId)
                {
                    _logger.LogWarning("Received InternalEnvelope without SourceDeviceId; skipping");
                    return new DeliverOpaqueMessageResult();
                }
                _logger.LogDebug("Parsed InternalEnvelope with case {Case}", internalEnvelope.ApplicationPayloadCase);
                InternalEnvelope? responseEnvelope = null;

                if (!AllowedCases.Contains(internalEnvelope.ApplicationPayloadCase))
                {
                    _logger.LogWarning("InternalEnvelope case {Case} not allowed in DeliverOpaque path", internalEnvelope.ApplicationPayloadCase);
                    return new DeliverOpaqueMessageResult();
                }
                _logger.LogDebug("Allowed InternalEnvelope case {Case}; dispatching to orchestrator/transport path", internalEnvelope.ApplicationPayloadCase);

                // Extract sender context from InternalEnvelope for cryptographic operations
                var sourceDeviceId = new DeviceId(internalEnvelope.SourceDeviceId);
                var identityRemotePeerId = new PeerId(directSession.RemotePeerId.Value);
                var ctx = new SessionContext(inferredSessionId.Value, request.SelfIdentityId, identityRemotePeerId, sourceDeviceId);

                // Special-case: RelayOpaqueEnvelope requires RPC-level ack response
                if (internalEnvelope.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope)
                {
                    var relay = internalEnvelope.RelayOpaqueEnvelope;
                    // Process the inner opaque payload (this may establish sessions and send responder msg via MessageService)
                    var relayHostPeerId = new Percolator.Identity.PeerId(directSession.RemotePeerId.Value);
                    await _mediator.Send(new ProcessRelayedOpaquePayloadCommand(
                        request.SelfIdentityId,
                        Payload.FromBytesOwned(relay.OpaquePayload.ToByteArray()),
                        // Relay host is the remote peer for this direct session (Host as known by this node)
                        relayHostPeerId,
                        sourceDeviceId), cancellationToken).ConfigureAwait(false);

                    // Build RPC-level RelayOpaqueResponse (not wrapped inside InternalEnvelope)
                    var ack = new RelayOpaqueResponse
                    {
                        Version = 1,
                        MessageAckId = relay.MessageAckId
                    };
                    // Encrypt ack bytes directly as RPC response payload
                    var ackPlain = Plaintext.FromBytesOwned(ack.ToByteArray());
                    var ackCipher = await _secureMessaging.EncryptAsync(inferredSessionId, ackPlain, cancellationToken).ConfigureAwait(false);
                    var ackBytes = ackCipher.ToArray();
                    return new DeliverOpaqueMessageResult { ResponsePayloadBytes = ackBytes };
                }

                var processed = await _mediator.Send(new ProcessInternalEnvelopeCommand(internalEnvelope, ctx), cancellationToken).ConfigureAwait(false);


                // Signal: peer online. Attempt relay of queued messages one-by-one until empty or first failure.
                try
                {
                    var identityPeerId = new Percolator.Identity.PeerId(remotePeerId.Value);
                    while (await _relayOrchestrator.RelayNextAsync(request.SelfIdentityId, identityPeerId, cancellationToken).ConfigureAwait(false))
                    {
                        // continue while acked
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Relay loop stopped due to failure; will resume on next online signal for {PeerId}", remotePeerId);
                }

                if (processed is not null)
                {
                    var earlyBytes = await EncryptResponseEnvelope(inferredSessionId, processed).ConfigureAwait(false);
                    return new DeliverOpaqueMessageResult { ResponsePayloadBytes = earlyBytes };
                }
                
                if (responseEnvelope is null)
                {
                    return new DeliverOpaqueMessageResult();
                }
                var responseBytes = await EncryptResponseEnvelope(inferredSessionId, responseEnvelope).ConfigureAwait(false);
                return new DeliverOpaqueMessageResult { ResponsePayloadBytes = responseBytes };
            
        }
        private async Task<byte[]> EncryptResponseEnvelope(SessionId sessionId, InternalEnvelope internalEnvelope)
        {
            var plaintext = Plaintext.FromBytes(internalEnvelope.ToByteArray());
            var ratchetMessage = await _secureMessaging.EncryptAsync(sessionId, plaintext).ConfigureAwait(false);
            return ratchetMessage.ToArray();
        }
    }
}
