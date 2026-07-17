using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Identity;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network;

public class OpaqueMessageDeliverer : IOpaqueMessageDeliverer
{
    private readonly ILogger<OpaqueMessageDeliverer> _logger;
    private readonly IMediator _mediator;
    private readonly IDirectSessionRepository _directSessionRepository;
    private readonly IRatchetKeyIndex _ratchetLookup;
    private readonly ISecureMessagingService _secureMessaging;
    private static readonly HashSet<InternalEnvelope.ApplicationPayloadOneofCase> AllowedCases = new()
    {
        InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope,
        InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope,
        InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope,
        InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope,
        InternalEnvelope.ApplicationPayloadOneofCase.SubmitPreKeyBundleResponse,
        InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse,
        InternalEnvelope.ApplicationPayloadOneofCase.EnqueueOpaqueMessageResponse
    };

    public OpaqueMessageDeliverer(
        ILogger<OpaqueMessageDeliverer> logger,
        IMediator mediator,
        IDirectSessionRepository directSessionRepository,
        IRatchetKeyIndex ratchetLookup,
        ISecureMessagingService secureMessaging)
    {
        _logger = logger;
        _mediator = mediator;
        _directSessionRepository = directSessionRepository;
        _ratchetLookup = ratchetLookup;
        _secureMessaging = secureMessaging;
    }

    public async Task<bool> DeliverAsync(byte[] opaqueBytes, CancellationToken ct)
    {
        try
        {
            var sessionRatchetMessage = SessionRatchetMessage.FromBytes(opaqueBytes);
            var header = sessionRatchetMessage.GetHeader();
            var ratchetKey = header.PreKey;

            // Get self identity from the ratchet message context
            var selfIdentityId = sessionRatchetMessage.GetSelfId();
            var resolved = await _secureMessaging.DecryptInboundAsync(new CryptoSelfId(selfIdentityId), sessionRatchetMessage, ct).ConfigureAwait(false);
            
            if (resolved is null)
            {
                _logger.LogWarning("Decrypt returned null; message delivery failed");
                return false;
            }

            var inferredSessionId = resolved.Value.sessionId;
            var plaintext = resolved.Value.plaintext;
            var nonNullDirectSessionId = new DirectSessionId(inferredSessionId.Value);

            if (plaintext is null)
            {
                _logger.LogWarning("Decryption resulted in null plaintext for session {SessionId}. This may be a skipped message.", inferredSessionId);
                return false;
            }

            await _ratchetLookup.UpsertAsync(new CryptoSelfId(selfIdentityId), inferredSessionId, ratchetKey, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

            var directSession = await _directSessionRepository.GetBySessionIdAsync(nonNullDirectSessionId, new NetworkSelfId(selfIdentityId)).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No direct session mapping found for session {inferredSessionId}");

            var internalEnvelope = InternalEnvelope.Parser.ParseFrom(plaintext.Span);
            if (internalEnvelope.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.None)
            {
                _logger.LogWarning("Received unhandled one-of message type: {MessageType}", internalEnvelope.ApplicationPayloadCase);
                return false;
            }

            if (!internalEnvelope.HasSourceDeviceId)
            {
                _logger.LogWarning("Received InternalEnvelope without SourceDeviceId; skipping");
                return false;
            }

            if (!AllowedCases.Contains(internalEnvelope.ApplicationPayloadCase))
            {
                _logger.LogWarning("InternalEnvelope case {Case} not allowed in DeliverOpaque path", internalEnvelope.ApplicationPayloadCase);
                return false;
            }

            // Extract sender context from InternalEnvelope for cryptographic operations
            var sourceDeviceId = new DeviceId(internalEnvelope.SourceDeviceId);
            var identityRemotePeerId = new PeerId(directSession.RemoteNetworkPeerId.Value);
            var ctx = new SessionContext(inferredSessionId.Value, new Percolator.Identity.PublicIdentityId(selfIdentityId), identityRemotePeerId, sourceDeviceId);

            // Dispatch the envelope
            await _mediator.Send(new ProcessInternalEnvelopeCommand(internalEnvelope, ctx), ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error delivering opaque message");
            return false;
        }
    }
}
