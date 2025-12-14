using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Contracts;

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

        public InitiatorFinalizeService(
            ILogger<InitiatorFinalizeService> logger,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active,
            IPreHandshakeSessionStore prehandshake,
            ISessionRepository sessions,
            IRatchetKeyIndex index,
            IClock clock)
        {
            _logger = logger;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
            _prehandshake = prehandshake;
            _sessions = sessions;
            _index = index;
            _clock = clock;
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
                        PeerId.NewId(),
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
