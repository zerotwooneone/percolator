using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Contracts;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    internal sealed class InitiatorFinalizeService : IInitiatorFinalizeService
    {
        private readonly ILogger<InitiatorFinalizeService> _logger;
        private readonly IActiveIdentityAccessor _activeIdentityAccessor;
        private readonly ActiveIdentityContext _active;
        private readonly IPreHandshakeSessionStore _prehandshake;
        private readonly ISessionRepository _sessions;
        private readonly IRatchetKeyIndex _index;
        private readonly IClock _clock;
        private readonly ISessionCrypto _sessionCrypto;

        public InitiatorFinalizeService(
            ILogger<InitiatorFinalizeService> logger,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active,
            IPreHandshakeSessionStore prehandshake,
            ISessionRepository sessions,
            IRatchetKeyIndex index,
            IClock clock,
            ISessionCrypto sessionCrypto)
        {
            _logger = logger;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
            _prehandshake = prehandshake;
            _sessions = sessions;
            _index = index;
            _clock = clock;
            _sessionCrypto = sessionCrypto;
        }

        public async Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromInviteHandshakeResponseAsync(
            InviteHandshakeResponse response,
            CancellationToken cancellationToken = default)
        {
            if (response is null) throw new ArgumentNullException(nameof(response));

            if (!_activeIdentityAccessor.IsActive || _active.Identity is null || _active.Keys is null)
                throw new InvalidOperationException("Active identity not loaded.");

            if (!response.HasAcceptorIdentityKey || response.AcceptorIdentityKey.Length == 0)
                throw new InvalidOperationException("acceptor_identity_key is required.");

            if (!response.HasAcceptorX3DhEphemeralKey || response.AcceptorX3DhEphemeralKey.Length == 0)
                throw new InvalidOperationException("acceptor_x3dh_ephemeral_key is required.");

            if (!response.HasInitialRatchetMessage || response.InitialRatchetMessage.Length == 0)
                throw new InvalidOperationException("initial_ratchet_message is required.");

            // This is the initiator's view (the inviter who sent the signed pre-key). We derive the shared secret
            // using X3DH_Respond and then decrypt the acceptor's first ratchet message.
            var initiatorIdentityPublic = new RatchetIdentityKey(response.AcceptorIdentityKey.ToByteArray());
            var initiatorEphemeralPublic = new RatchetEphemeralKey(response.AcceptorX3DhEphemeralKey.ToByteArray());

            var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
            var localSpkPriv = new PrivatePreKey(_active.Keys.SignedPreKey.ExportECPrivateKey());

            var shared = _sessionCrypto.X3DH_Respond(
                initiatorIdentityPublic,
                initiatorEphemeralPublic,
                localIkPriv,
                localSpkPriv,
                localOtkPrivate: null);

            var root = new RootKey(shared.Value);

            var ratchetMessage = new SessionRatchetMessage(response.InitialRatchetMessage.ToByteArray());
            var header = ratchetMessage.GetHeader();

            // Acceptor sent first message from an initiator session, so we must bootstrap as responder to decrypt.
            var tmp = RatchetBootstrap.CreateResponderSession(
                SessionId.NewId(),
                Percolator.Cryptography.Primitives.PeerId.NewId(),
                new ProtocolVersion(1),
                root,
                _clock);

            Plaintext pt;
            try
            {
                pt = tmp.Decrypt(ratchetMessage, _clock);
            }
            catch
            {
                return null;
            }

            ResponderInnerHello inner;
            try
            {
                inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
            }
            catch
            {
                return null;
            }

            if (!inner.HasVersion || inner.Version != 1) return null;
            if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId)) return null;

            SessionId sid;
            try
            {
                sid = new SessionId(Guid.Parse(inner.DirectSessionId));
            }
            catch
            {
                return null;
            }

            var final = SecureSession.Create(
                sid,
                tmp.RemotePeerId,
                tmp.ProtocolVersion,
                tmp.State,
                new AeadSessionCrypto(),
                _clock);

            await _sessions.AddAsync(final, cancellationToken).ConfigureAwait(false);
            await _index.UpsertAsync(sid, header.PreKey, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

            // Best-effort cleanup of legacy prehandshake store (if it was populated)
            try
            {
                var mostRecent = await _prehandshake.TryGetMostRecentAsync(_active.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
                if (mostRecent is not null)
                {
                    await _prehandshake.DeleteAsync(mostRecent.Id, _active.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }

            _logger.LogInformation("Initiator finalized session {SessionId} from InviteHandshakeResponse", sid.Value);
            return (sid, pt);
        }

        public async Task<(SessionId sessionId, Plaintext plaintext)?> TryFinalizeFromFirstResponderAsync(
            SessionRatchetMessage responderFirst,
            CancellationToken cancellationToken = default)
        {
            if (!_activeIdentityAccessor.IsActive || _active.Identity is null)
                throw new InvalidOperationException("Active identity not loaded.");

            // Header's pre-key used to upsert on success
            var header = responderFirst.GetHeader();
            var headerPreKey = header.PreKey;

            await foreach (var pending in _prehandshake.EnumeratePendingAsync(_active.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var root = new RootKey(pending.InitialRootKey);
                    var tmp = RatchetBootstrap.CreateInitiatorSession(
                        SessionId.NewId(),
                        Percolator.Cryptography.Primitives.PeerId.NewId(),
                        new ProtocolVersion(1),
                        root,
                        _clock);

                    var pt = tmp.Decrypt(responderFirst, _clock);

                    // Parse inner payload for responder-assigned session id
                    var inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
                    if (!inner.HasVersion || inner.Version != 1)
                        throw new InvalidOperationException("Responder inner payload version invalid.");
                    if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
                        throw new InvalidOperationException("Responder inner payload missing direct_session_id.");
                    var sid = new SessionId(Guid.Parse(inner.DirectSessionId));

                    // Create final initiator session with progressed state
                    var final = SecureSession.Create(
                        sid,
                        tmp.RemotePeerId,
                        tmp.ProtocolVersion,
                        tmp.State,
                        new AeadSessionCrypto(),
                        _clock);

                    await _sessions.AddAsync(final, cancellationToken).ConfigureAwait(false);
                    await _index.UpsertAsync(sid, headerPreKey, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                    await _prehandshake.DeleteAsync(pending.Id, _active.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("Initiator finalized session {SessionId} from pending record {PendingId}", sid.Value, pending.Id);
                    return (sid, pt);
                }
                catch
                {
                    // Try next pending record
                }
            }

            return null;
        }
    }
}
