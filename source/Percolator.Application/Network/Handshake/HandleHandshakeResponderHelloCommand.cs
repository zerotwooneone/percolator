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
        private readonly Percolator.Application.Network.IRatchetKeySessionLookup _ratchetLookup;
        private readonly IPreHandshakeSessionStore _preHandshakeStore;

        public HandleHandshakeResponderHelloHandler(
            ILogger<HandleHandshakeResponderHelloHandler> logger,
            IDirectSessionManager sessions,
            ActiveIdentityContext active,
            Percolator.Application.Network.IRatchetKeySessionLookup ratchetLookup,
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
            var directSessionId = await _ratchetLookup.TryResolveAsync(header.PreKey, _active.Identity.SelfIdentityId, cancellationToken);
            if (directSessionId is null)
            {
                // Delegate slow-path finalize to the session manager. It will persist the session, upsert the ratchet index,
                // and delete the matching prehandshake record if found.
                var (sid, pt) = await _sessions.CompleteHandshakeAsync(
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
                    cancellationToken);
                directSessionId = new Percolator.Network.DirectSessionId(sid.Value);
                _logger.LogInformation("Responder hello slow-path succeeded for session {SessionId}", sid.Value);
            }

            // Decrypt using explicit session (fast-path)
            var resolvedSid = new SessionId(directSessionId.Value.Value);
            var plaintext = await _sessions.ReceiveMessageAsync(resolvedSid, ratchetMessage);
            if (plaintext is null)
            {
                throw new InvalidOperationException("Responder hello: unable to decrypt with resolved session.");
            }

            _logger.LogInformation("Successfully processed responder hello for session {SessionId}", directSessionId.Value.Value);
        }
    }
}
