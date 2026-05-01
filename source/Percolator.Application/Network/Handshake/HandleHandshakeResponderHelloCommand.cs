using MediatR;
using Percolator.Contracts;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Network.Handshake
{
    // Finalizes the initiator-side of the X3DH/DR establishment when a HandshakeResponderHello arrives via relay/MQ.
    public record HandleHandshakeResponderHelloCommand(
        SelfId SelfIdentityId,
        InviteHandshakeResponse Response
    ) : IRequest;

    internal class HandleHandshakeResponderHelloHandler : IRequestHandler<HandleHandshakeResponderHelloCommand>
    {
        private readonly ILogger<HandleHandshakeResponderHelloHandler> _logger;
        private readonly IRatchetKeyIndex _ratchetLookup;
        private readonly IInitiatorFinalizeService _finalize;

        public HandleHandshakeResponderHelloHandler(
            ILogger<HandleHandshakeResponderHelloHandler> logger,
            IRatchetKeyIndex ratchetLookup,
            IInitiatorFinalizeService finalize)
        {
            _logger = logger;
            _ratchetLookup = ratchetLookup;
            _finalize = finalize;
        }

        public async Task Handle(HandleHandshakeResponderHelloCommand request, CancellationToken cancellationToken)
        {
            if (request.Response is null)
            {
                throw new InvalidOperationException("InviteHandshakeResponse is required.");
            }

            if (!request.Response.HasInitialRatchetMessage || request.Response.InitialRatchetMessage.Length == 0)
            {
                throw new InvalidOperationException("initial_ratchet_message is required.");
            }

            // Parse the encrypted payload as a SessionRatchetMessage
            var ratchetMessage = SessionRatchetMessage.FromBytes(request.Response.InitialRatchetMessage.ToByteArray());
            var header = ratchetMessage.GetHeader();

            // Fast-path: resolve session by ratchet header key (expected to miss on first responder message)
            var sessionId = await _ratchetLookup.TryResolveAsync(request.SelfIdentityId.Value, header.PreKey, cancellationToken).ConfigureAwait(false);
            Percolator.Network.DirectSessionId directSessionId;
            if (sessionId is null)
            {
                var finalized = await _finalize.TryFinalizeFromInviteHandshakeResponseAsync(request.SelfIdentityId, request.Response, cancellationToken).ConfigureAwait(false)
                    ?? await _finalize.TryFinalizeFromFirstResponderAsync(request.SelfIdentityId, ratchetMessage, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Unable to finalize initiator session from responder hello");
                var sid = finalized.sessionId;
                directSessionId = new Percolator.Network.DirectSessionId(sid.Value);
                _logger.LogInformation("Responder hello slow-path finalized; using responder-provided direct_session_id {SessionId}", sid.Value);
            }
            else
            {
                directSessionId = new Percolator.Network.DirectSessionId(sessionId.Value);
            }

            _logger.LogInformation("Successfully processed responder hello for session {SessionId}", directSessionId.Value);
        }
    }
}
