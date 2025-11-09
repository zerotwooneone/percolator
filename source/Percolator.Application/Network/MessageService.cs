using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
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
        private readonly IDirectSessionManager _sessionManager;
        private readonly ActiveIdentityContext _active;
        private readonly INetworkSender _networkSender;

        public MessageService(
            ILogger<MessageService> logger,
            IDirectSessionRepository sessions,
            IDirectSessionManager sessionManager,
            ActiveIdentityContext active,
            INetworkSender networkSender)
        {
            _logger = logger;
            _sessions = sessions;
            _sessionManager = sessionManager;
            _active = active;
            _networkSender = networkSender;
        }

        public async Task<(SendResult Result, DeliverOpaqueMessageResponse? Response)> SendMessageWithResponseAsync(
            InternalEnvelope envelope,
            PeerId recipientPeerId,
            CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            // Require an existing direct session to encrypt the envelope to the recipient.
            var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
            if (ds is null)
            {
                return (SendResult.CreateFailure(Array.Empty<string>(), attempts: 0, lastError: new InvalidOperationException("No direct session to recipient")), null);
            }

            var sessionId = new SessionId(ds.SessionId.Value);
            var cipher = await _sessionManager.EncryptMessageAsync(sessionId, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);

            var outcome = await _networkSender
                .SendAsync(new Percolator.Network.PeerId(recipientPeerId.Value), new NetworkPayload(cipher.Value), SendStrategy.DirectThenRelay, ct)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                return (SendResult.CreateFailure(outcome.AttemptedPaths.ToArray(), outcome.Attempts, outcome.LastError), null);
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
                        ResponsePayload = Google.Protobuf.ByteString.CopyFrom(outcome.ResponsePayload.Value.Value.ToArray())
                    }
                };
            }
            return (SendResult.CreateSuccess(outcome.Path, outcome.AttemptedPaths.ToArray(), outcome.Attempts), resp);
        }

        public async Task<SendResult> SendPreEncryptedAsync(PeerId recipientPeerId, SessionRatchetMessage cipher, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            // Use Network domain sender with DirectThenRelay strategy. Payload must be the recipient-targeted DR ciphertext.
            var target = new Percolator.Network.PeerId(recipientPeerId.Value);
            var payload = new NetworkPayload(cipher.Value);
            var outcome = await _networkSender.SendAsync(target, payload, SendStrategy.DirectThenRelay, ct).ConfigureAwait(false);

            if (outcome.Success)
            {
                return SendResult.CreateSuccess(outcome.Path, outcome.AttemptedPaths.ToArray(), attempts: outcome.Attempts);
            }
            return SendResult.CreateFailure(outcome.AttemptedPaths.ToArray(), attempts: outcome.Attempts, lastError: outcome.LastError);
        }

        public async Task<SendResult> SendMessageAsync(InternalEnvelope envelope, PeerId recipientPeerId, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            // Require an existing direct session to build recipient DR ciphertext
            var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
            if (ds is null)
            {
                return SendResult.CreateFailure(Array.Empty<string>(), attempts: 0, lastError: new InvalidOperationException("No direct session to recipient"));
            }

            var sessionId = new SessionId(ds.SessionId.Value);
            var cipher = await _sessionManager.EncryptMessageAsync(sessionId, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);
            var outcome = await _networkSender
                .SendAsync(new Percolator.Network.PeerId(recipientPeerId.Value), new NetworkPayload(cipher.Value), SendStrategy.DirectThenRelay, ct)
                .ConfigureAwait(false);

            if (outcome.Success)
            {
                return SendResult.CreateSuccess(outcome.Path, outcome.AttemptedPaths.ToArray(), outcome.Attempts);
            }
            return SendResult.CreateFailure(outcome.AttemptedPaths.ToArray(), outcome.Attempts, outcome.LastError);
        }
    }
}
