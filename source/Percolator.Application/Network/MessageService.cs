using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Network
{
    public sealed class MessageService : IMessageService
    {
        private readonly ILogger<MessageService> _logger;
        private readonly IDirectSessionRepository _sessions;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IMessageTransportService _transport;
        private readonly ActiveIdentityContext _active;
        private readonly Percolator.Identity.IPeerRepository _peers;
        private readonly IPeerPublicSigningKeyStore _keyStore;

        public MessageService(
            ILogger<MessageService> logger,
            IDirectSessionRepository sessions,
            IDirectSessionManager sessionManager,
            IMessageTransportService transport,
            ActiveIdentityContext active,
            Percolator.Identity.IPeerRepository peers,
            IPeerPublicSigningKeyStore keyStore)
        {
            _logger = logger;
            _sessions = sessions;
            _sessionManager = sessionManager;
            _transport = transport;
            _active = active;
            _peers = peers;
            _keyStore = keyStore;
        }

        public async Task<(SendResult Result, Percolator.Contracts.DeliverOpaqueMessageResponse? Response)> SendMessageWithResponseAsync(
            InternalEnvelope envelope,
            Percolator.Identity.PeerId recipientPeerId,
            CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            var attempted = new System.Collections.Generic.List<string>();
            try
            {
                attempted.Add("Direct");
                var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (ds is null)
                {
                    attempted.Add("Relay");
                    await TryHostEnqueueAsync(recipientPeerId, envelope, null, ct).ConfigureAwait(false);
                    return (SendResult.Success("Relay", attempted.ToArray(), attempts: 1), null);
                }

                var sessionId = new SessionId(ds.SessionId.Value);
                var directSessionId = new DirectSessionId(ds.SessionId.Value);
                var cipher = await _sessionManager.EncryptMessageAsync(sessionId, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);
                try
                {
                    var resp = await _transport.SendMessageAsync(recipientPeerId, directSessionId, cipher, ct).ConfigureAwait(false);
                    return (SendResult.Success("Direct", attempted.ToArray(), attempts: 1), resp);
                }
                catch (Exception sendEx)
                {
                    _logger.LogDebug(sendEx, "Direct send failed to {PeerId}; attempting host enqueue if possible", recipientPeerId);
                    attempted.Add("Relay");
                    await TryHostEnqueueAsync(recipientPeerId, envelope, cipher, ct).ConfigureAwait(false);
                    return (SendResult.Success("Relay", attempted.ToArray(), attempts: 2), null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipientPeerId);
                return (SendResult.Failure(attempted.ToArray(), attempts: attempted.Count, lastError: ex), null);
            }
        }

        public async Task<SendResult> SendMessageAsync(InternalEnvelope envelope, Percolator.Identity.PeerId recipientPeerId, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            var attempted = new System.Collections.Generic.List<string>();
            try
            {
                attempted.Add("Direct");
                // Prefer existing direct session
                var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (ds is null)
                {
                    // No direct session, attempt relay if possible
                    attempted.Add("Relay");
                    await TryHostEnqueueAsync(recipientPeerId, envelope, null, ct).ConfigureAwait(false);
                    return SendResult.Success("Relay", attempted.ToArray(), attempts: 1);
                }

                var sessionId = new SessionId(ds.SessionId.Value);
                var directSessionId = new DirectSessionId(ds.SessionId.Value);
                var cipher = await _sessionManager.EncryptMessageAsync(sessionId, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);
                try
                {
                    await _transport.SendMessageAsync(recipientPeerId, directSessionId, cipher, ct).ConfigureAwait(false);
                    return SendResult.Success("Direct", attempted.ToArray(), attempts: 1);
                }
                catch (Exception sendEx)
                {
                    _logger.LogDebug(sendEx, "Direct send failed to {PeerId}; attempting host enqueue if possible", recipientPeerId);
                    attempted.Add("Relay");
                    await TryHostEnqueueAsync(recipientPeerId, envelope, cipher, ct).ConfigureAwait(false);
                    return SendResult.Success("Relay", attempted.ToArray(), attempts: 2);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipientPeerId);
                return SendResult.Failure(attempted.ToArray(), attempts: attempted.Count, lastError: ex);
            }
        }

        private async Task TryHostEnqueueAsync(Percolator.Identity.PeerId recipientPeerId, InternalEnvelope envelope, SessionRatchetMessage? recipientCipher, CancellationToken ct)
        {
            try
            {
                // Resolve recipient PKH from store
                var pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientPeerId, ct).ConfigureAwait(false);
                if (pkh is null || pkh.Length == 0)
                {
                    _logger.LogDebug("Cannot enqueue to host for {PeerId}: missing recipient PKH", recipientPeerId);
                    return;
                }

                // We need a session to the host to send the enqueue request
                var host = await _peers.GetByNameAsync("host").ConfigureAwait(false);
                if (host is null)
                {
                    _logger.LogDebug("Host peer not found; cannot enqueue on behalf of {PeerId}", recipientPeerId);
                    return;
                }
                var hostPeerId = new Percolator.Identity.PeerId(host.Id.Value);
                var hostDs = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(hostPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (hostDs is null)
                {
                    _logger.LogDebug("No direct session to host; cannot enqueue on behalf of {PeerId}", recipientPeerId);
                    return;
                }

                byte[] blobBytes;
                if (recipientCipher is not null)
                {
                    blobBytes = recipientCipher.Value;
                }
                else
                {
                    // Attempt to encrypt to recipient; if still no session, bail.
                    var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                    if (ds is null)
                    {
                        _logger.LogDebug("Cannot produce DR ciphertext for {PeerId}; enqueue aborted", recipientPeerId);
                        return;
                    }
                    var sid = new SessionId(ds.SessionId.Value);
                    var tmpCipher = await _sessionManager.EncryptMessageAsync(sid, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);
                    blobBytes = tmpCipher.Value;
                }

                var mqReq = new EnqueueOpaqueMessageRequest
                {
                    Version = 1,
                    RecipientPublicKeyHash = ByteString.CopyFrom(pkh),
                    MessageBlob = ByteString.CopyFrom(blobBytes)
                };
                var toHost = new InternalEnvelope
                {
                    MessageQueueEnvelope = new MessageQueueEnvelope
                    {
                        Version = 1,
                        EnqueueOpaqueMessageRequest = mqReq
                    }
                };

                var hostPlain = new Plaintext(toHost.ToByteArray());
                var hostSessionId = new SessionId(hostDs.SessionId.Value);
                var hostDirectSessionId = new DirectSessionId(hostDs.SessionId.Value);
                var hostCipher = await _sessionManager.EncryptMessageAsync(hostSessionId, hostPlain).ConfigureAwait(false);
                await _transport.SendMessageAsync(hostPeerId, hostDirectSessionId, hostCipher, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host enqueue fallback failed for {PeerId}", recipientPeerId);
            }
        }
    }
}
