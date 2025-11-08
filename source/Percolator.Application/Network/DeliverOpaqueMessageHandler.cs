using System.Security.Cryptography;
using System.Collections.Generic;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Sessions;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;
using Percolator.Prekey.Handlers;
using Percolator.Application.Network.Handshake;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.Application.Network
{
    public class DeliverOpaqueMessageHandler : IRequestHandler<DeliverOpaqueMessageCommand, DeliverOpaqueMessageResult>
    {
        private readonly ILogger<DeliverOpaqueMessageHandler> _logger;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IPeerConnectionRepository _peerConnectionRepository;
        private readonly IMediator _mediator;
        private readonly IDirectSessionRepository _directSessionRepository;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IRatchetKeySessionLookup _ratchetLookup;
        private readonly RelayOrchestrator _relayOrchestrator;
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
            IDirectSessionManager sessionManager,
            IPeerConnectionRepository peerConnectionRepository,
            IMediator mediator,
            IDirectSessionRepository directSessionRepository,
            ActiveIdentityContext activeIdentityContext,
            IRatchetKeySessionLookup ratchetLookup,
            RelayOrchestrator relayOrchestrator)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _peerConnectionRepository = peerConnectionRepository;
            _mediator = mediator;
            _directSessionRepository = directSessionRepository;
            _activeIdentityContext = activeIdentityContext;
            _ratchetLookup = ratchetLookup;
            _relayOrchestrator = relayOrchestrator;
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
                        SignedPreKeyId = new Guid(upload.SignedPreKeyId.ToByteArray()),
                        SignedPreKey = upload.SignedPreKey.ToByteArray(),
                        PreKeySignature = upload.PreKeySignature.ToByteArray(),
                        OneTimePreKeys = upload.OneTimePreKeys.Select(x => new SubmitPreKeyBundleCommand.OneTimePreKey(
                            new Guid(x.Id.ToByteArray()), x.PublicKey.ToByteArray())).ToList(),
                        Expires = upload.ExpiresUtc.ToDateTimeOffset(),
                        RemotePeerId = remotePeerId
                    };
                    await _mediator.Send(cmd, ct).ConfigureAwait(false);
                    return new InternalEnvelope { SubmitPreKeyBundleResponse = new SubmitPreKeyBundleResponse { Version = 1 } };
                case PrekeyEnvelope.MessageOneofCase.GetPreKeyBundleRequest:
                    var getReq = prekeyEnvelope.GetPreKeyBundleRequest;
                    if (!getReq.HasPublicKeyHash) throw new InvalidOperationException("PublicKeyHash is required");
                    var bundle = await _mediator.Send(new Percolator.Prekey.Handlers.GetPreKeyBundleQuery
                    {
                        TargetPublicSigningKeyHash = getReq.PublicKeyHash.ToByteArray()
                    }, ct).ConfigureAwait(false);
                    var resp = new GetPreKeyBundleResponse { Version = 1 };
                    if (bundle is not null)
                    {
                        var msg = new GetPreKeyBundleResponse.Types.PreKeyBundle
                        {
                            Version = 1,
                            IdentityKey = ByteString.CopyFrom(bundle.IdentitySigningKey.Value),
                            SignedPreKeyId = ByteString.CopyFrom(bundle.SignedPreKeyId.ToByteArray()),
                            SignedPreKey = ByteString.CopyFrom(bundle.SignedPreKey.Value),
                            PreKeySignature = ByteString.CopyFrom(bundle.SignedPreKeySignature.Value)
                        };
                        if (bundle.OneTimePreKey is not null)
                        {
                            msg.OneTimeKeyId = ByteString.CopyFrom(bundle.OneTimePreKeyId!.Value.ToByteArray());
                            msg.OneTimeKey = ByteString.CopyFrom(bundle.OneTimePreKey.Value);
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
            _logger.LogInformation("Processing opaque message (session inferred from ratchet header)");
            
                var sessionRatchetMessage = new SessionRatchetMessage(request.PayloadBytes);
                var header = sessionRatchetMessage.GetHeader();
                var ratchetKey = header.PreKey;
                var resolvedDirectSessionId = await _ratchetLookup.TryResolveAsync(ratchetKey, _activeIdentityContext.Identity!.SelfIdentityId, cancellationToken).ConfigureAwait(false);
                Plaintext? plaintext;
                SessionId inferredSessionId;
                DirectSessionId nonNullDirectSessionId;
                if (resolvedDirectSessionId is not null)
                {
                    nonNullDirectSessionId = resolvedDirectSessionId.Value;
                    inferredSessionId = new SessionId(nonNullDirectSessionId.Value);
                    _logger.LogDebug("Fast-path lookup hit for ratchet header key; inferred session {SessionId}", inferredSessionId);
                    plaintext = await _sessionManager.ReceiveMessageAsync(inferredSessionId, sessionRatchetMessage).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("Fast-path lookup MISS for ratchet header key; attempting slow-path inference");
                    var inferResult = await _sessionManager.TryInferAndReceiveAsync(sessionRatchetMessage, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Unable to resolve session by ratchet header key or slow-path inference");
                    inferredSessionId = inferResult.sessionId;
                    plaintext = inferResult.plaintext;
                    _logger.LogInformation("Slow-path inference SUCCEEDED; inferred session {SessionId}", inferredSessionId);
                    nonNullDirectSessionId = new DirectSessionId(inferredSessionId.Value);
                }
                if (plaintext is null)
                {
                    _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", inferredSessionId);
                    return new DeliverOpaqueMessageResult();
                }

                await _ratchetLookup.UpsertAsync(nonNullDirectSessionId, _activeIdentityContext.Identity!.SelfIdentityId, ratchetKey, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

                var directSession = await _directSessionRepository.GetBySessionIdAsync(nonNullDirectSessionId, _activeIdentityContext.Identity.SelfIdentityId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"No direct session mapping found for session {inferredSessionId}");
                var remotePeerId = directSession.RemotePeerId;
                _logger.LogInformation("Resolved remote peer {PeerId} for session {SessionId}", remotePeerId, directSession.SessionId);
                var connectionInfo = await _peerConnectionRepository.GetByIdAsync(remotePeerId).ConfigureAwait(false);
                if (connectionInfo?.GrpcEndPoints.FirstOrDefault() is null)
                {
                    _logger.LogWarning("Could not find connection info for peer {PeerId} to handle opaque message", remotePeerId);
                    return new DeliverOpaqueMessageResult();
                }

                var endpoint = connectionInfo.GrpcEndPoints.First();
                _logger.LogInformation("Using endpoint {Endpoint} for peer {PeerId}", endpoint, remotePeerId);

                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
                _logger.LogDebug("Parsed InternalEnvelope with case {Case}", internalEnvelope.ApplicationPayloadCase);
                InternalEnvelope? responseEnvelope = null;

                if (!AllowedCases.Contains(internalEnvelope.ApplicationPayloadCase))
                {
                    _logger.LogWarning("InternalEnvelope case {Case} not allowed in DeliverOpaque path", internalEnvelope.ApplicationPayloadCase);
                    return new DeliverOpaqueMessageResult();
                }
                _logger.LogDebug("Allowed InternalEnvelope case {Case}; dispatching to orchestrator/transport path", internalEnvelope.ApplicationPayloadCase);

                var ctx = new SessionContext(inferredSessionId.Value, _activeIdentityContext.Identity!.SelfIdentityId, directSession.RemotePeerId.Value);

                // Special-case: RelayOpaqueEnvelope requires RPC-level ack response
                if (internalEnvelope.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope)
                {
                    var relay = internalEnvelope.RelayOpaqueEnvelope;
                    // Process the inner opaque payload (this may establish sessions and send responder msg via MessageService)
                    await _mediator.Send(new ProcessRelayedOpaquePayloadCommand(
                        new Payload(relay.OpaquePayload.ToByteArray()),
                        // Relay host is the remote peer for this direct session (Host as known by this node)
                        new Percolator.Identity.PeerId(directSession.RemotePeerId.Value)), cancellationToken).ConfigureAwait(false);

                    // Build RPC-level RelayOpaqueResponse (not wrapped inside InternalEnvelope)
                    var ack = new RelayOpaqueResponse
                    {
                        Version = 1,
                        MessageAckId = relay.MessageAckId
                    };
                    // Encrypt ack bytes directly as RPC response payload
                    var ackPlain = new Plaintext(ack.ToByteArray());
                    var ackCipher = await _sessionManager.EncryptMessageAsync(inferredSessionId, ackPlain).ConfigureAwait(false);
                    var ackBytes = ackCipher.Value;
                    return new DeliverOpaqueMessageResult { ResponsePayloadBytes = ackBytes };
                }

                var processed = await _mediator.Send(new ProcessInternalEnvelopeCommand(internalEnvelope, ctx), cancellationToken).ConfigureAwait(false);

                connectionInfo.UpdateLastSeen(endpoint, DateTimeOffset.UtcNow);
                await _peerConnectionRepository.SaveAsync(connectionInfo).ConfigureAwait(false);

                // Signal: peer online. Attempt relay of queued messages one-by-one until empty or first failure.
                try
                {
                    var identityPeerId = new Percolator.Identity.PeerId(remotePeerId.Value);
                    while (await _relayOrchestrator.RelayNextAsync(identityPeerId, cancellationToken).ConfigureAwait(false))
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
            var plaintext = new Plaintext(internalEnvelope.ToByteArray());
            var ratchetMessage = await _sessionManager.EncryptMessageAsync(sessionId, plaintext).ConfigureAwait(false);
            return ratchetMessage.Value;
        }
    }
}
