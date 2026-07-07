using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network.Messaging;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Network.Messaging;

namespace Percolator.Application.Network;

// Bridges Network domain sender to existing Application transport and crypto/session services
public sealed class RouteSender : IRouteSender
{
    private readonly ILogger<RouteSender> _logger;
    private readonly IMessageTransportService _transport;
    private readonly IDirectSessionRepository _sessions;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly ActiveIdentityContext _active;
    private readonly IPeerPublicSigningKeyStore _keyStore;

    public RouteSender(
        ILogger<RouteSender> logger,
        IMessageTransportService transport,
        IDirectSessionRepository sessions,
        ISecureMessagingService secureMessaging,
        ActiveIdentityContext active,
        IPeerPublicSigningKeyStore keyStore)
    {
        _logger = logger;
        _transport = transport;
        _sessions = sessions;
        _secureMessaging = secureMessaging;
        _active = active;
        _keyStore = keyStore;
    }

    public async Task<TransportSendResult> SendDirectAsync(Percolator.Network.PeerId target, NetworkPayload payload, CancellationToken ct = default)
    {
        try
        {
            if (_active.Identity is null) return new TransportSendResult(false, null, SendFailureReason.Unknown, new InvalidOperationException("Active identity not initialized"), null);
            var ds = await _sessions.GetByRemotePeerIdAsync(target, new NetworkSelfId(_active.Identity.SelfIdentityId.Value)).ConfigureAwait(false);
            if (ds is null) return new TransportSendResult(false, null, SendFailureReason.NoPeerConnection, null, null);

            var directSessionId = new DirectSessionId(ds.SessionId.Value);
            var recipientIdentityPeerId = new Percolator.Identity.PeerId(target.Value);
            var cipher = SessionRatchetMessage.FromSpan(payload.Value.Span);
            var resp = await _transport.SendMessageAsync(recipientIdentityPeerId, directSessionId, cipher, ct).ConfigureAwait(false);
            
            // Extract used endpoint from response
            System.Net.DnsEndPoint? usedEndpoint = resp.UsedEndpoint?.EndPoint;
            
            if (resp.OriginalResponse?.ResponsePayload is not null)
            {
                return new TransportSendResult(true, new NetworkPayload(resp.OriginalResponse.ResponsePayload.ResponsePayload.ToByteArray()), null, null, usedEndpoint);
            }
            return new TransportSendResult(true, null, null, null, usedEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct transport attempt failed to {PeerId}", target);
            return new TransportSendResult(false, null, SendFailureReason.TransportUnavailable, ex, null);
        }
    }

    public async Task<TransportSendResult> SendViaRelayAsync(Percolator.Network.PeerId relay, Percolator.Network.PeerId target, NetworkPayload payload, CancellationToken ct = default)
    {
        try
        {
            if (_active.Identity is null) return new TransportSendResult(false, null, SendFailureReason.Unknown, new InvalidOperationException("Active identity not initialized"), null);
            // Must have a direct session to the relay host
            var relaySession = await _sessions.GetByRemotePeerIdAsync(relay, new NetworkSelfId(_active.Identity.SelfIdentityId.Value)).ConfigureAwait(false);
            if (relaySession is null) return new TransportSendResult(false, null, SendFailureReason.NoRelaySession, null, null);

            // We need recipient PKH to enqueue
            var recipientIdentityPeerId = new Percolator.Identity.PeerId(target.Value);
            var pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientIdentityPeerId, ct).ConfigureAwait(false);
            if (pkh is null) return new TransportSendResult(false, null, SendFailureReason.NoPeerConnection, null, null);

            var mqReq = new EnqueueOpaqueMessageRequest
            {
                Version = 1,
                RecipientPublicKeyHash = Google.Protobuf.ByteString.CopyFrom(pkh.ToArray()),
                MessageBlob = Google.Protobuf.ByteString.CopyFrom(payload.Value.ToArray())
            };
            var toRelay = new InternalEnvelope
            {
                MessageQueueEnvelope = new MessageQueueEnvelope
                {
                    Version = 1,
                    EnqueueOpaqueMessageRequest = mqReq
                }
            };

            var relayPlain = Plaintext.FromBytes(toRelay.ToByteArray());
            var relaySessionId = new SessionId(relaySession.SessionId.Value);
            var relayDirectSessionId = new DirectSessionId(relaySession.SessionId.Value);
            var relayCipher = await _secureMessaging.EncryptAsync(relaySessionId, relayPlain, ct).ConfigureAwait(false);
            var resp = await _transport.SendMessageAsync(new Percolator.Identity.PeerId(relay.Value), relayDirectSessionId, relayCipher, ct).ConfigureAwait(false);
            if (resp.OriginalResponse?.ResponsePayload is not null)
            {
                return new TransportSendResult(true, new NetworkPayload(resp.OriginalResponse.ResponsePayload.ResponsePayload.ToByteArray()), null, null, null);
            }
            return new TransportSendResult(true, null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay transport attempt failed via {Relay} for {Target}", relay, target);
            return new TransportSendResult(false, null, SendFailureReason.TransportUnavailable, ex, null);
        }
    }
}
