using System;
using System.Collections.Generic;
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
    public sealed class RemoteEnvelopeSender : IRemoteEnvelopeSender
    {
        private readonly ILogger<RemoteEnvelopeSender> _logger;
        private readonly IDirectSessionRepository _sessions;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IMessageTransportService _transport;
        private readonly ActiveIdentityContext _active;
        private readonly Percolator.Identity.IPeerRepository _peers;

        public RemoteEnvelopeSender(
            ILogger<RemoteEnvelopeSender> logger,
            IDirectSessionRepository sessions,
            IDirectSessionManager sessionManager,
            IMessageTransportService transport,
            ActiveIdentityContext active,
            Percolator.Identity.IPeerRepository peers)
        {
            _logger = logger;
            _sessions = sessions;
            _sessionManager = sessionManager;
            _transport = transport;
            _active = active;
            _peers = peers;
        }

        public async Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            var internalEnvelope = new InternalEnvelope { ChatEnvelope = chatEnvelope };
            var plaintext = new Plaintext(internalEnvelope.ToByteArray());
            try
            {
                // Prefer existing direct session
                var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipient.PeerId.Value), _active.Identity!.SelfIdentityId);
                if (ds is null)
                {
                    _logger.LogWarning("No direct session to {PeerId}; attempting host enqueue fallback if possible", recipient.PeerId);
                    await TryHostEnqueueAsync(recipient, plaintext, null, ct);
                    return;
                }

                var sessionId = new SessionId(ds.SessionId.Value);
                var directSessionId = new DirectSessionId(ds.SessionId.Value);
                var cipher = await _sessionManager.EncryptMessageAsync(sessionId, plaintext);
                try
                {
                    await _transport.SendMessageAsync(recipient.PeerId, directSessionId, cipher, ct);
                }
                catch (Exception sendEx)
                {
                    _logger.LogWarning(sendEx, "Direct send failed to {PeerId}; attempting host enqueue fallback if possible", recipient.PeerId);
                    await TryHostEnqueueAsync(recipient, plaintext, cipher, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipient.PeerId);
            }
        }

        private async Task TryHostEnqueueAsync(RecipientRoute recipient, Plaintext plaintext, SessionRatchetMessage? recipientCipher, CancellationToken ct)
        {
            try
            {
                if (recipient.PublicKeyHash is null || recipient.PublicKeyHash.Length == 0)
                {
                    _logger.LogDebug("Cannot enqueue to host for {PeerId}: missing recipient PKH", recipient.PeerId);
                    return;
                }

                // We need a session to the host to send the enqueue request
                var host = await _peers.GetByNameAsync("host");
                if (host is null)
                {
                    _logger.LogDebug("Host peer not found; cannot enqueue on behalf of {PeerId}", recipient.PeerId);
                    return;
                }
                var hostPeerId = new Percolator.Identity.PeerId(host.Id.Value);
                var hostDs = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(hostPeerId.Value), _active.Identity!.SelfIdentityId);
                if (hostDs is null)
                {
                    _logger.LogDebug("No direct session to host; cannot enqueue on behalf of {PeerId}", recipient.PeerId);
                    return;
                }

                // Use recipientCipher if provided (preferred). If null, try to encrypt now (requires recipient session).
                byte[] blobBytes;
                if (recipientCipher is not null)
                {
                    blobBytes = recipientCipher.Value;
                }
                else
                {
                    // Attempt to encrypt to recipient; if still no session, bail.
                    var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipient.PeerId.Value), _active.Identity!.SelfIdentityId);
                    if (ds is null)
                    {
                        _logger.LogDebug("Cannot produce DR ciphertext for {PeerId}; enqueue aborted", recipient.PeerId);
                        return;
                    }
                    var sid = new SessionId(ds.SessionId.Value);
                    var tmpCipher = await _sessionManager.EncryptMessageAsync(sid, plaintext);
                    blobBytes = tmpCipher.Value;
                }

                var mqReq = new Percolator.Contracts.EnqueueOpaqueMessageRequest
                {
                    Version = 1,
                    RecipientPublicKeyHash = ByteString.CopyFrom(recipient.PublicKeyHash),
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
                var hostCipher = await _sessionManager.EncryptMessageAsync(hostSessionId, hostPlain);
                await _transport.SendMessageAsync(hostPeerId, hostDirectSessionId, hostCipher, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Host enqueue fallback failed for {PeerId}", recipient.PeerId);
            }
        }
    }
}
