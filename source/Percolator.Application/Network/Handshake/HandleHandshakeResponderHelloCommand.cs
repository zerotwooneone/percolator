using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Google.Protobuf;
using Percolator.Contracts;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    // Finalizes the initiator-side of the X3DH/DR establishment when a HandshakeResponderHello arrives via relay/MQ.
    public record HandleHandshakeResponderHelloCommand(
        byte[] EncryptedPayload
    ) : IRequest;

    internal class HandleHandshakeResponderHelloHandler : IRequestHandler<HandleHandshakeResponderHelloCommand>
    {
        private readonly ILogger<HandleHandshakeResponderHelloHandler> _logger;
        private readonly IDirectSessionManager _sessions;
        private readonly ActiveIdentityContext _active;
        private readonly IRatchetKeyIndex _ratchetLookup;
        private readonly IPreHandshakeSessionStore _preHandshakeStore;

        public HandleHandshakeResponderHelloHandler(
            ILogger<HandleHandshakeResponderHelloHandler> logger,
            IDirectSessionManager sessions,
            ActiveIdentityContext active,
            IRatchetKeyIndex ratchetLookup,
            IPreHandshakeSessionStore preHandshakeStore)
        {
            _logger = logger;
            _sessions = sessions;
            _active = active;
            _ratchetLookup = ratchetLookup;
            _preHandshakeStore = preHandshakeStore;
        }

        public async Task Handle(HandleHandshakeResponderHelloCommand request, CancellationToken cancellationToken)
        {
            if (_active.Identity is null)
            {
                throw new InvalidOperationException("Active identity not loaded.");
            }

            // Parse the encrypted payload as a SessionRatchetMessage
            var ratchetMessage = new SessionRatchetMessage(request.EncryptedPayload);
            var header = ratchetMessage.GetHeader();

            // Fast-path: resolve session by ratchet header key (expected to miss on first responder message)
            var sessionId = await _ratchetLookup.TryResolveAsync(header.PreKey, cancellationToken).ConfigureAwait(false);
            Percolator.Network.DirectSessionId directSessionId;
            if (sessionId is null)
            {
                // Delegate slow-path finalize to the session manager. It will persist the session, upsert the ratchet index,
                // and delete the matching prehandshake record if found.
                var (sid, plaintext) = await _sessions.CompleteHandshakeAsync(
                    ratchetMessage,
                    pt =>
                    {
                        var inner = ResponderInnerHello.Parser.ParseFrom(pt.Value);
                        if (!inner.HasVersion || inner.Version != 1)
                            throw new InvalidOperationException("Responder inner payload version invalid.");
                        if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
                            throw new InvalidOperationException("Responder inner payload missing direct_session_id.");
                        return new SessionId(Guid.Parse(inner.DirectSessionId));
                    },
                    cancellationToken).ConfigureAwait(false);
                directSessionId = new Percolator.Network.DirectSessionId(sid.Value);
                _logger.LogInformation("Responder hello slow-path succeeded for session {SessionId}", sid.Value);
            }
            else
            {
                directSessionId = new Percolator.Network.DirectSessionId(sessionId.Value);
            }

            _logger.LogInformation("Successfully processed responder hello for session {SessionId}", directSessionId.Value);
        }
    }
}
