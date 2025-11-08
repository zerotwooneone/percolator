using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network
{
    public sealed class MessageService : IMessageService
    {
        private readonly ILogger<MessageService> _logger;
        private readonly IDirectSessionRepository _sessions;
        private readonly IDirectSessionManager _sessionManager;
        private readonly IMessageTransportService _transport;
        private readonly ActiveIdentityContext _active;
        private readonly IPeerRepository _peers;
        private readonly IPeerPublicSigningKeyStore _keyStore;
        private readonly IPeerConnectionRepository _peerConnectionRepository;

        public MessageService(
            ILogger<MessageService> logger,
            IDirectSessionRepository sessions,
            IDirectSessionManager sessionManager,
            IMessageTransportService transport,
            ActiveIdentityContext active,
            IPeerRepository peers,
            IPeerPublicSigningKeyStore keyStore,
            IPeerConnectionRepository peerConnectionRepository)
        {
            _logger = logger;
            _sessions = sessions;
            _sessionManager = sessionManager;
            _transport = transport;
            _active = active;
            _peers = peers;
            _keyStore = keyStore;
            _peerConnectionRepository = peerConnectionRepository;
        }

        public async Task<(SendResult Result, DeliverOpaqueMessageResponse? Response)> SendMessageWithResponseAsync(
            InternalEnvelope envelope,
            PeerId recipientPeerId,
            CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            var attempted = new List<string>();
            try
            {
                attempted.Add("Direct");
                var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (ds is null)
                {
                    attempted.Add("Relay");
                    var tryRelayResult = await TryRelayEnqueueAsync(recipientPeerId, envelope, null, ct).ConfigureAwait(false);
                    if(tryRelayResult.Success)
                    {
                        return (SendResult.CreateSuccess("Relay", attempted.ToArray(), attempts: 1), null);
                    }
                    return (SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: tryRelayResult.LastError), null);
                }

                var sessionId = new SessionId(ds.SessionId.Value);
                var directSessionId = new DirectSessionId(ds.SessionId.Value);
                var cipher = await _sessionManager.EncryptMessageAsync(sessionId, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);
                try
                {
                    var resp = await _transport.SendMessageAsync(recipientPeerId, directSessionId, cipher, ct).ConfigureAwait(false);
                    return (SendResult.CreateSuccess("Direct", attempted.ToArray(), attempts: 1), resp);
                }
                catch (Exception sendEx)
                {
                    _logger.LogDebug(sendEx, "Direct send failed to {PeerId}; attempting relay enqueue if possible", recipientPeerId);
                    attempted.Add("Relay");
                    var tryRelayEnqueueResult = await TryRelayEnqueueAsync(recipientPeerId, envelope, cipher, ct).ConfigureAwait(false);
                    if(tryRelayEnqueueResult.Success)
                    {
                        return (SendResult.CreateSuccess("Relay", attempted.ToArray(), attempts: 2), null);
                    }
                    return (SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: tryRelayEnqueueResult.LastError), null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipientPeerId);
                return (SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: ex), null);
            }
            return (SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: new InvalidOperationException("all attempts to send failed")), null);
        }

        public async Task<SendResult> SendPreEncryptedAsync(PeerId recipientPeerId, SessionRatchetMessage cipher, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            var attempted = new List<string>();
            try
            {
                attempted.Add("Direct");
                var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (ds is null)
                {
                    attempted.Add("Relay");
                    
                    // We already have a DR ciphertext targeting the recipient
                    var tryRelayEnqueueResult = await TryRelayEnqueueAsync(recipientPeerId, new InternalEnvelope(), cipher, ct).ConfigureAwait(false);
                    if(tryRelayEnqueueResult.Success)
                    {
                        return SendResult.CreateSuccess("Relay", attempted.ToArray(), attempts: 1);
                    }
                    return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: tryRelayEnqueueResult.LastError);
                }

                var directSessionId = new DirectSessionId(ds.SessionId.Value);
                try
                {
                    await _transport.SendMessageAsync(recipientPeerId, directSessionId, cipher, ct).ConfigureAwait(false);
                    return SendResult.CreateSuccess("Direct", attempted.ToArray(), attempts: 1);
                }
                catch (Exception sendEx)
                {
                    _logger.LogDebug(sendEx, "Direct send (pre-encrypted) failed to {PeerId}; attempting relay enqueue if possible", recipientPeerId);
                    attempted.Add("Relay");
                    var tryRelayEnqueueResult = await TryRelayEnqueueAsync(recipientPeerId, new InternalEnvelope(), cipher, ct).ConfigureAwait(false);
                    if(tryRelayEnqueueResult.Success)
                    {
                        return SendResult.CreateSuccess("Relay", attempted.ToArray(), attempts: 2);
                    }
                    return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: tryRelayEnqueueResult.LastError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending pre-encrypted message to {PeerId}", recipientPeerId);
                return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: ex);
            }
            return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: new InvalidOperationException("all attempts to send preencrypted failed"));
        }

        public async Task<SendResult> SendMessageAsync(InternalEnvelope envelope, PeerId recipientPeerId, CancellationToken ct = default)
        {
            if (_active.Identity is null)
                throw new InvalidOperationException("Active identity not initialized");

            var attempted = new List<string>();
            try
            {
                attempted.Add("Direct");
                // Prefer existing direct session
                var ds = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (ds is null)
                {
                    // No direct session, attempt relay if possible
                    attempted.Add("Relay");
                    var tryRelayEnqueueResult = await TryRelayEnqueueAsync(recipientPeerId, envelope, null, ct).ConfigureAwait(false);
                    if(tryRelayEnqueueResult.Success)
                    {
                        return SendResult.CreateSuccess("Relay", attempted.ToArray(), attempts: 1);
                    }
                    return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: tryRelayEnqueueResult.LastError);
                }

                var sessionId = new SessionId(ds.SessionId.Value);
                var directSessionId = new DirectSessionId(ds.SessionId.Value);
                var cipher = await _sessionManager.EncryptMessageAsync(sessionId, new Plaintext(envelope.ToByteArray())).ConfigureAwait(false);
                try
                {
                    await _transport.SendMessageAsync(recipientPeerId, directSessionId, cipher, ct).ConfigureAwait(false);
                    return SendResult.CreateSuccess("Direct", attempted.ToArray(), attempts: 1);
                }
                catch (Exception sendEx)
                {
                    _logger.LogDebug(sendEx, "Direct send failed to {PeerId}; attempting relay enqueue if possible", recipientPeerId);
                    attempted.Add("Relay");
                    var tryRelayEnqueueResult = await TryRelayEnqueueAsync(recipientPeerId, envelope, cipher, ct).ConfigureAwait(false);
                    if(tryRelayEnqueueResult.Success)
                    {
                        return SendResult.CreateSuccess("Relay", attempted.ToArray(), attempts: 2);
                    }
                    return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: tryRelayEnqueueResult.LastError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipientPeerId);
                return SendResult.CreateFailure(attempted.ToArray(), attempts: attempted.Count, lastError: ex);
            }
        }

        private async Task<(bool Success, Exception? LastError)> TryRelayEnqueueAsync(PeerId recipientPeerId, InternalEnvelope envelope, SessionRatchetMessage? recipientCipher, CancellationToken ct)
        {
            try
            {
                // Resolve recipient PKH from store
                var connection = await _peerConnectionRepository.GetByIdAsync(new Percolator.Network.PeerId(recipientPeerId.Value)).ConfigureAwait(false);
                if (connection is null || connection.RelayPeerId is null)
                {
                    _logger.LogDebug("Peer connection not found for {PeerId}", recipientPeerId);
                    return (false, null);
                }

                var pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientPeerId, ct).ConfigureAwait(false);
                if (pkh is null)
                {
                    _logger.LogDebug("Recipient PKH not found for {PeerId}", recipientPeerId);
                    return (false, null);
                }

                // We need a session to the relay to send the enqueue request
                var relayPeer = await _peers.GetByIdAsync(new PeerId(connection.RelayPeerId.Value)).ConfigureAwait(false);
                if (relayPeer is null)
                {
                    _logger.LogDebug("Relay peer not found; cannot enqueue on behalf of {PeerId}", recipientPeerId);
                    return (false, null);
                }
                var relaySession = await _sessions.GetByRemotePeerIdAsync(new Percolator.Network.PeerId(relayPeer.Id.Value), _active.Identity!.SelfIdentityId).ConfigureAwait(false);
                if (relaySession is null)
                {
                    _logger.LogDebug("No direct session to relay; cannot enqueue on behalf of {PeerId}", recipientPeerId);
                    return (false, null);
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
                        return (false, null);
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
                var toRelay = new InternalEnvelope
                {
                    MessageQueueEnvelope = new MessageQueueEnvelope
                    {
                        Version = 1,
                        EnqueueOpaqueMessageRequest = mqReq
                    }
                };

                var relayPlain = new Plaintext(toRelay.ToByteArray());
                var relaySessionId = new SessionId(relaySession.SessionId.Value);
                var relayDirectSessionId = new DirectSessionId(relaySession.SessionId.Value);
                var relayCipher = await _sessionManager.EncryptMessageAsync(relaySessionId, relayPlain).ConfigureAwait(false);
                await _transport.SendMessageAsync(relayPeer.Id, relayDirectSessionId, relayCipher, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Relay enqueue fallback failed for {PeerId}", recipientPeerId);
                return (false, ex);
            }

            return (true, null);
        }
    }
}
