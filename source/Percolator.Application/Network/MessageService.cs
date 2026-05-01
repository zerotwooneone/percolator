using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;
using Percolator.Network.Messaging;

namespace Percolator.Application.Network
{
    public sealed class MessageService : IMessageService
    {
        private readonly ILogger<MessageService> _logger;
        private readonly IDirectSessionRepository _sessions;
        private readonly ISecureMessagingService _secureMessaging;
        private readonly ActiveIdentityContext _active;
        private readonly INetworkSender _networkSender;
        private readonly IOutboundMessageWireTap _wireTap;
        private readonly IPeerPublicSigningKeyStore _keyStore;

        public MessageService(
            ILogger<MessageService> logger,
            IDirectSessionRepository sessions,
            ISecureMessagingService secureMessaging,
            ActiveIdentityContext active,
            INetworkSender networkSender,
            IOutboundMessageWireTap wireTap,
            IPeerPublicSigningKeyStore keyStore)
        {
            _logger = logger;
            _sessions = sessions;
            _secureMessaging = secureMessaging;
            _active = active;
            _networkSender = networkSender;
            _wireTap = wireTap;
            _keyStore = keyStore;
        }

        public async Task<(SendResult Result, DeliverOpaqueMessageResponse? Response)> SendMessageWithResponseAsync(
            InternalEnvelope envelope,
            PeerId recipientPeerId,
            CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            // Require an existing direct session to encrypt the envelope to the recipient.
            var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId.Value).ConfigureAwait(false);
            if (ds is null)
            {
                return (SendResult.CreateFailure(Array.Empty<string>(), attempts: 0, lastError: new InvalidOperationException("No direct session to recipient")), null);
            }

            var sessionId = new SessionId(ds.SessionId.Value);
            var cipher = await _secureMessaging.EncryptAsync(sessionId, Plaintext.FromBytes(envelope.ToByteArray()), ct).ConfigureAwait(false);
            var cipherBytes = cipher.ToArray();

            if (_wireTap.Enabled)
            {
                if (_wireTap.Mode == SimulatorOutboundMode.SimulateOnly)
                {
                    const string sendPath = "Simulated";
                    _wireTap.Tap(new OutboundWireMessage(
                        DestinationPeerId: new Percolator.Network.PeerId(recipientPeerId.Value),
                        SendPath: sendPath,
                        MessageType: "EncryptedEnvelope",
                        RequestCorrelationId: null,
                        PayloadBytes: cipherBytes,
                        PayloadLength: cipherBytes.Length));
                    return (SendResult.CreateSuccess(sendPath, new[] { sendPath }, attempts: 0), null);
                }
            }

            var outcome = await _networkSender
                .SendAsync(_active.Identity!.SelfIdentityId.Value, new Percolator.Network.PeerId(recipientPeerId.Value), new NetworkPayload(cipherBytes), SendStrategy.DirectThenRelay, ct)
                .ConfigureAwait(false);

            if (_wireTap.Enabled)
            {
                _wireTap.Tap(new OutboundWireMessage(
                    DestinationPeerId: new Percolator.Network.PeerId(recipientPeerId.Value),
                    SendPath: outcome.Path,
                    MessageType: "EncryptedEnvelope",
                    RequestCorrelationId: null,
                    PayloadBytes: cipherBytes,
                    PayloadLength: cipherBytes.Length));
            }

            if (!outcome.Success)
            {
                var attemptedPaths = outcome.AttemptedPaths.ToArray();
                return (SendResult.CreateFailure(attemptedPaths, outcome.Attempts, outcome.LastError), null);
            }

            // Map optional response payload into DeliverOpaqueMessageResponse if present
            DeliverOpaqueMessageResponse? resp = null;
            if (outcome.ResponsePayload is not null)
            {
                resp = new DeliverOpaqueMessageResponse
                {
                    Version = 1,
                    ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                    {
                        Version = 1,
                        ResponsePayload = Google.Protobuf.ByteString.CopyFrom(outcome.ResponsePayload.Value.Value.Span)
                    }
                };
            }
            var attemptedPaths2 = outcome.AttemptedPaths.ToArray();
            return (SendResult.CreateSuccess(outcome.Path, attemptedPaths2, outcome.Attempts), resp);
        }

        public async Task<SendResult> SendMessageAsync(InternalEnvelope envelope, PeerId recipientPeerId, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            // Require an existing direct session to build recipient DR ciphertext
            var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId.Value).ConfigureAwait(false);
            if (ds is null)
            {
                return SendResult.CreateFailure(Array.Empty<string>(), attempts: 0, lastError: new InvalidOperationException("No direct session to recipient"));
            }

            var sessionId = new SessionId(ds.SessionId.Value);
            var cipher = await _secureMessaging.EncryptAsync(sessionId, Plaintext.FromBytes(envelope.ToByteArray()), ct).ConfigureAwait(false);
            var cipherBytes = cipher.ToArray();

            if (_wireTap.Enabled)
            {
                if (_wireTap.Mode == SimulatorOutboundMode.SimulateOnly)
                {
                    const string sendPath = "Simulated";
                    _wireTap.Tap(new OutboundWireMessage(
                        DestinationPeerId: new Percolator.Network.PeerId(recipientPeerId.Value),
                        SendPath: sendPath,
                        MessageType: "EncryptedEnvelope",
                        RequestCorrelationId: null,
                        PayloadBytes: cipherBytes,
                        PayloadLength: cipherBytes.Length));
                    return SendResult.CreateSuccess(sendPath, new[] { sendPath }, attempts: 0);
                }
            }

            var outcome = await _networkSender
                .SendAsync(_active.Identity!.SelfIdentityId.Value, new Percolator.Network.PeerId(recipientPeerId.Value), new NetworkPayload(cipherBytes), SendStrategy.DirectThenRelay, ct)
                .ConfigureAwait(false);

            if (_wireTap.Enabled)
            {
                _wireTap.Tap(new OutboundWireMessage(
                    DestinationPeerId: new Percolator.Network.PeerId(recipientPeerId.Value),
                    SendPath: outcome.Path,
                    MessageType: "EncryptedEnvelope",
                    RequestCorrelationId: null,
                    PayloadBytes: cipherBytes,
                    PayloadLength: cipherBytes.Length));
            }

            if (outcome.Success)
            {
                var attemptedPaths = outcome.AttemptedPaths.ToArray();
                return SendResult.CreateSuccess(outcome.Path, attemptedPaths, outcome.Attempts);
            }
            var attemptedPaths2 = outcome.AttemptedPaths.ToArray();
            return SendResult.CreateFailure(attemptedPaths2, outcome.Attempts, outcome.LastError);
        }
    }
}
