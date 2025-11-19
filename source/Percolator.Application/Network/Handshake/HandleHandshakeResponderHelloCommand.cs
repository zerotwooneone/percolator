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
        private readonly Percolator.Application.Services.ISecureMessagingService _secure;
        private readonly ActiveIdentityContext _active;
        private readonly IRatchetKeyIndex _ratchetLookup;
        private readonly IPreHandshakeSessionStore _preHandshakeStore;

        public HandleHandshakeResponderHelloHandler(
            ILogger<HandleHandshakeResponderHelloHandler> logger,
            IDirectSessionManager sessions,
            Percolator.Application.Services.ISecureMessagingService secure,
            ActiveIdentityContext active,
            IRatchetKeyIndex ratchetLookup,
            IPreHandshakeSessionStore preHandshakeStore)
        {
            _logger = logger;
            _sessions = sessions;
            _secure = secure;
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
                var decrypt = await _secure.DecryptInboundAsync(ratchetMessage, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Unable to decrypt responder hello");
                var plaintext = decrypt.plaintext;
                var inner = ResponderInnerHello.Parser.ParseFrom(plaintext.Value);
                if (!inner.HasVersion || inner.Version != 1)
                    throw new InvalidOperationException("Responder inner payload version invalid.");
                if (!inner.HasDirectSessionId || string.IsNullOrWhiteSpace(inner.DirectSessionId))
                    throw new InvalidOperationException("Responder inner payload missing direct_session_id.");
                var sid = new SessionId(Guid.Parse(inner.DirectSessionId));
                // Session establishment will be finalized by higher-level handshake orchestration using pre-handshake state.
                directSessionId = new Percolator.Network.DirectSessionId(sid.Value);
                _logger.LogInformation("Responder hello slow-path parsed direct_session_id {SessionId}", sid.Value);
            }
            else
            {
                directSessionId = new Percolator.Network.DirectSessionId(sessionId.Value);
            }

            _logger.LogInformation("Successfully processed responder hello for session {SessionId}", directSessionId.Value);
        }
    }
}
