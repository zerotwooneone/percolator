using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Cryptography;

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
                // Slow-path: iterate Pending pre-handshake entries and attempt decrypt
                await foreach (var record in _preHandshakeStore.EnumeratePendingAsync(_active.Identity.SelfIdentityId, cancellationToken))
                {
                    var inferred = await _sessions.TryInferAndReceiveAsync(ratchetMessage, cancellationToken);
                    if (inferred is not null)
                    {
                        // Upsert ratchet header key -> session mapping and cleanup pre-handshake record
                        await _ratchetLookup.UpsertAsync(new Percolator.Network.DirectSessionId(inferred.Value.sessionId.Value), _active.Identity.SelfIdentityId, header.PreKey, DateTimeOffset.UtcNow, cancellationToken);
                        await _preHandshakeStore.DeleteAsync(record.Id, _active.Identity.SelfIdentityId, cancellationToken);
                        _logger.LogInformation("Responder hello slow-path succeeded for session {SessionId}", inferred.Value.sessionId.Value);
                        //todo: need to create and store the session
                        return;
                    }
                }

                throw new InvalidOperationException("Responder hello: unable to resolve session via fast or slow path.");
            }

            // Decrypt using explicit session (fast-path)
            var plaintext = await _sessions.ReceiveMessageAsync(new SessionId(directSessionId.Value.Value), ratchetMessage);
            if (plaintext is null)
            {
                throw new InvalidOperationException("Responder hello: unable to decrypt with resolved session.");
            }

            _logger.LogInformation("Successfully processed responder hello for session {SessionId}", directSessionId.Value.Value);
        }
    }
}
