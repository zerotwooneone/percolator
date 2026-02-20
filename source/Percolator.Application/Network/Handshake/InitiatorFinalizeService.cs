using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
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
        private readonly ISentInvitationRepository _sentInvitations;
        private readonly ISelfPreKeyBundleRepository _selfPreKeys;

        public InitiatorFinalizeService(
            ILogger<InitiatorFinalizeService> logger,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active,
            IPreHandshakeSessionStore prehandshake,
            ISessionRepository sessions,
            IRatchetKeyIndex index,
            IClock clock,
            ISessionCrypto sessionCrypto,
            ISentInvitationRepository sentInvitations,
            ISelfPreKeyBundleRepository selfPreKeys)
        {
            _logger = logger;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
            _prehandshake = prehandshake;
            _sessions = sessions;
            _index = index;
            _clock = clock;
            _sessionCrypto = sessionCrypto;
            _sentInvitations = sentInvitations;
            _selfPreKeys = selfPreKeys;
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
            var acceptorIdentityPublic = new RatchetIdentityKey(response.AcceptorIdentityKey.ToByteArray());
            var acceptorEphemeralPublic = new RatchetEphemeralKey(response.AcceptorX3DhEphemeralKey.ToByteArray());

            var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());

            // IMPORTANT: For reverse-signal, the inviter's signed pre-key used in the invite may be
            // different from _active.Keys.SignedPreKey. Resolve the correct private key via SentInvitation.
            PrivatePreKey localSpkPriv;
            try
            {
                if (!response.HasRequestCorrelationId
                    || !Guid.TryParse(response.RequestCorrelationId, out var corrGuid)
                    || corrGuid == Guid.Empty)
                {
                    _logger.LogWarning("Invite finalize: missing/invalid request_correlation_id; skipping invite-response finalize.");
                    return null;
                }

                if (_active.Identity is null)
                {
                    _logger.LogWarning("Invite finalize: active identity missing; skipping invite-response finalize.");
                    return null;
                }

                var sent = await _sentInvitations.TryGetAsync(new RequestCorrelationId(corrGuid), cancellationToken).ConfigureAwait(false);
                if (sent is null)
                {
                    // This commonly happens for peer->main simulator flows (pinv), where main is the acceptor
                    // and therefore never created a matching SentInvitation.
                    _logger.LogInformation("Invite finalize: no sent invitation record found for correlation {CorrelationId}; skipping invite-response finalize.", corrGuid);
                    return null;
                }

                var spk = await _selfPreKeys.TryGetSignedPreKeyAsync(
                        _active.Identity.SelfIdentityId.Value,
                        sent.SignedPreKeyId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (spk is null)
                {
                    _logger.LogWarning("Invite finalize: could not load signed pre-key private for correlation {CorrelationId} (SignedPreKeyId={SignedPreKeyId}); skipping invite-response finalize.", corrGuid, sent.SignedPreKeyId);
                    return null;
                }

                localSpkPriv = new PrivatePreKey(spk.Value.spkPrivate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: failed to resolve signed pre-key private from sent invitation; skipping invite-response finalize.");
                return null;
            }

            SharedSecret shared;
            try
            {
                shared = _sessionCrypto.X3DH_Respond(
                    acceptorIdentityPublic,
                    acceptorEphemeralPublic,
                    localIkPriv,
                    localSpkPriv,
                    localOtkPrivate: null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: X3DH_Respond failed.");
                return null;
            }

            var root = new RootKey(shared.Value);

            SessionRatchetMessage ratchetMessage;
            try
            {
                ratchetMessage = new SessionRatchetMessage(response.InitialRatchetMessage.ToByteArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: initial_ratchet_message was not a valid SessionRatchetMessage.");
                return null;
            }

            (RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) header;
            try
            {
                header = ratchetMessage.GetHeader();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: SessionRatchetMessage header parse failed.");
                return null;
            }

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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: failed to decrypt initial ratchet message.");
                return null;
            }

            ResponderInnerHello inner;
            try
            {
                inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: decrypted plaintext was not a valid ResponderInnerHello.");
                return null;
            }

            if (!inner.HasVersion || inner.Version != 1)
            {
                _logger.LogWarning("Invite finalize: ResponderInnerHello version invalid (HasVersion={HasVersion}, Version={Version})", inner.HasVersion, inner.Version);
                return null;
            }
            if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
            {
                _logger.LogWarning("Invite finalize: ResponderInnerHello missing direct_session_id.");
                return null;
            }

            SessionId sid;
            try
            {
                sid = new SessionId(Guid.Parse(inner.DirectSessionId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invite finalize: direct_session_id was not a GUID: {DirectSessionId}", inner.DirectSessionId);
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
            await _index.UpsertAsync(_active.Identity.SelfIdentityId.Value, sid, header.PreKey, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

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
                    await _index.UpsertAsync(_active.Identity.SelfIdentityId.Value, sid, headerPreKey, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
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
