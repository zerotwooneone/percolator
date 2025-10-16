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
            InternalEnvelope.ApplicationPayloadOneofCase.EnqueueOpaqueMessageResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.FetchQueuedMessagesResponse
        };

        public DeliverOpaqueMessageHandler(
            ILogger<DeliverOpaqueMessageHandler> logger,
            IDirectSessionManager sessionManager,
            IPeerConnectionRepository peerConnectionRepository,
            IMediator mediator,
            IDirectSessionRepository directSessionRepository,
            ActiveIdentityContext activeIdentityContext,
            IRatchetKeySessionLookup ratchetLookup)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _peerConnectionRepository = peerConnectionRepository;
            _mediator = mediator;
            _directSessionRepository = directSessionRepository;
            _activeIdentityContext = activeIdentityContext;
            _ratchetLookup = ratchetLookup;
        }

        private async Task<SubmitPreKeyBundleResponse> HandlePrekeyEnvelopeAsync(PrekeyEnvelope prekeyEnvelope,
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
                    await _mediator.Send(cmd, ct);
                    break;
                default:
                    _logger.LogWarning("Received unhandled prekey message type: {MessageType}", prekeyEnvelope.MessageCase);
                    break;
            }

            return new SubmitPreKeyBundleResponse();
        }

        public async Task<DeliverOpaqueMessageResult> Handle(DeliverOpaqueMessageCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing opaque message (session inferred from ratchet header)");
            try
            {
                var sessionRatchetMessage = new SessionRatchetMessage(request.PayloadBytes);
                var header = sessionRatchetMessage.GetHeader();
                var ratchetKey = header.PreKey;
                var resolvedDirectSessionId = await _ratchetLookup.TryResolveAsync(ratchetKey, _activeIdentityContext.Identity!.SelfIdentityId, cancellationToken);
                Plaintext? plaintext;
                SessionId inferredSessionId;
                DirectSessionId nonNullDirectSessionId;
                if (resolvedDirectSessionId is not null)
                {
                    nonNullDirectSessionId = resolvedDirectSessionId.Value;
                    inferredSessionId = new SessionId(nonNullDirectSessionId.Value);
                    _logger.LogDebug("Fast-path lookup hit for ratchet header key; inferred session {SessionId}", inferredSessionId);
                    plaintext = await _sessionManager.ReceiveMessageAsync(inferredSessionId, sessionRatchetMessage);
                }
                else
                {
                    _logger.LogWarning("Fast-path lookup MISS for ratchet header key; attempting slow-path inference");
                    var inferResult = await _sessionManager.TryInferAndReceiveAsync(sessionRatchetMessage, cancellationToken)
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

                await _ratchetLookup.UpsertAsync(nonNullDirectSessionId, _activeIdentityContext.Identity!.SelfIdentityId, ratchetKey, DateTimeOffset.UtcNow, cancellationToken);

                var directSession = await _directSessionRepository.GetBySessionIdAsync(nonNullDirectSessionId, _activeIdentityContext.Identity.SelfIdentityId)
                    ?? throw new InvalidOperationException($"No direct session mapping found for session {inferredSessionId}");
                var remotePeerId = directSession.RemotePeerId;
                _logger.LogInformation("Resolved remote peer {PeerId} for session {SessionId}", remotePeerId, directSession.SessionId);
                var connectionInfo = await _peerConnectionRepository.GetByIdAsync(remotePeerId);
                if (connectionInfo?.GrpcEndPoints.FirstOrDefault() is null)
                {
                    _logger.LogWarning("Could not find connection info for peer {PeerId} to handle opaque message", remotePeerId);
                    return new DeliverOpaqueMessageResult();
                }

                var endpoint = connectionInfo.GrpcEndPoints.First();
                _logger.LogInformation("Using endpoint {Endpoint} for peer {PeerId}", endpoint, remotePeerId);

                var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
                InternalEnvelope? responseEnvelope = null;

                if (!AllowedCases.Contains(internalEnvelope.ApplicationPayloadCase))
                {
                    _logger.LogWarning("InternalEnvelope case {Case} not allowed in DeliverOpaque path", internalEnvelope.ApplicationPayloadCase);
                    return new DeliverOpaqueMessageResult();
                }

                var ctx = new SessionContext(inferredSessionId.Value, _activeIdentityContext.Identity!.SelfIdentityId, directSession.RemotePeerId.Value);
                var processed = await _mediator.Send(new ProcessInternalEnvelopeCommand(internalEnvelope, ctx), cancellationToken);

                connectionInfo.UpdateLastSeen(endpoint, DateTimeOffset.UtcNow);
                await _peerConnectionRepository.SaveAsync(connectionInfo);

                if (processed is not null)
                {
                    var earlyBytes = await EncryptResponseEnvelope(inferredSessionId, processed);
                    return new DeliverOpaqueMessageResult { ResponsePayloadBytes = earlyBytes };
                }

                switch (internalEnvelope.ApplicationPayloadCase)
                {
                    case InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope:
                        _logger.LogDebug("ChatEnvelope handled by orchestrator; no-op in transport handler");
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope:
                        responseEnvelope = await HandleDhtMessageAsync(internalEnvelope.DhtEnvelope, connectionInfo, endpoint, cancellationToken);
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope:
                        var response = await HandlePrekeyEnvelopeAsync(internalEnvelope.PrekeyEnvelope, connectionInfo.Id, cancellationToken);
                        responseEnvelope = new InternalEnvelope { SubmitPreKeyBundleResponse = response };
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope:
                        _logger.LogDebug("MessageQueueEnvelope handled by orchestrator; no-op in transport handler");
                        break;
                    case InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope:
                        var relay = internalEnvelope.RelayOpaqueEnvelope;
                        await _mediator.Send(new ProcessRelayedOpaquePayloadCommand(relay.OpaquePayload.ToByteArray()), cancellationToken);
                        break;
                    default:
                        _logger.LogWarning("Received unhandled internal envelope type: {EnvelopeType}", internalEnvelope.ApplicationPayloadCase);
                        break;
                }

                if (responseEnvelope is null)
                {
                    return new DeliverOpaqueMessageResult();
                }

                var responseBytes = await EncryptResponseEnvelope(inferredSessionId, responseEnvelope);
                return new DeliverOpaqueMessageResult { ResponsePayloadBytes = responseBytes };
            }
            catch (Exception drEx)
            {
                try
                {
                    var hello = HandshakeInitiatorHello.Parser.ParseFrom(request.PayloadBytes);
                    if (hello is not null && hello.HasInitiatorIdentityKeySpki && hello.HasInitiatorEphemeralKeySpki && hello.HasSignedPreKeyId)
                    {
                        var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
                        var eph = hello.InitiatorEphemeralKeySpki.ToByteArray();
                        var spkId = new Guid(hello.SignedPreKeyId.ToByteArray());
                        Guid? otkId = hello.HasOneTimePreKeyId ? new Guid(hello.OneTimePreKeyId.ToByteArray()) : (Guid?)null;

                        var responderBytes = await _mediator.Send(
                            new Percolator.Application.Network.Handshake.HandleHandshakeInitiatorHelloCommand(
                                spki,
                                eph,
                                spkId,
                                otkId,
                                null,
                                hello.HasEncryptedPayload ? hello.EncryptedPayload.ToByteArray() : null),
                            cancellationToken);

                        if (responderBytes is null || responderBytes.Length == 0)
                        {
                            return new DeliverOpaqueMessageResult();
                        }

                        return new DeliverOpaqueMessageResult { ResponsePayloadBytes = responderBytes };
                    }
                }
                catch
                {
                }
                _logger.LogError(drEx, "Error processing opaque message (DR path), and payload was not a valid HandshakeInitiatorHello");
                throw;
            }
        }

        private async Task<InternalEnvelope?> HandleDhtMessageAsync(DhtEnvelope dhtEnvelope, PeerConnection peerConnection, GrpcEndPoint endPoint, CancellationToken ct)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("No active identity available");
                return null;
            }

            switch (dhtEnvelope.MessageCase)
            {
                case DhtEnvelope.MessageOneofCase.PingRequest:
                    if (peerConnection.IdentitySigningKey is null)
                    {
                        _logger.LogWarning("Could not find identity signing key for peer {PeerId} to handle DHT message", peerConnection.Id);
                        return null;
                    }
                    var nodeIdBytes = SHA256.HashData(peerConnection.IdentitySigningKey.Value);
                    await _mediator.Send(new Percolator.Dht.Messages.PingRequest(new Percolator.Dht.NodeId(nodeIdBytes), endPoint.EndPoint), ct);
                    break;
                case DhtEnvelope.MessageOneofCase.FindNodeRequest:
                    // Centralized in ProcessInternalEnvelopeHandler; no-op here.
                    break;
                default:
                    _logger.LogWarning("Received unhandled DHT message type: {MessageType}", dhtEnvelope.MessageCase);
                    break;
            }

            return null;
        }

        private async Task<byte[]> EncryptResponseEnvelope(SessionId sessionId, InternalEnvelope internalEnvelope)
        {
            var plaintext = new Plaintext(internalEnvelope.ToByteArray());
            var ratchetMessage = await _sessionManager.EncryptMessageAsync(sessionId, plaintext);
            return ratchetMessage.Value;
        }
    }
}
