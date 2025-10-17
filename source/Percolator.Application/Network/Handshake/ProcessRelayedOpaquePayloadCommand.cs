using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using System.Collections.Generic;

namespace Percolator.Application.Network.Handshake
{
    // Client-side processor for opaque relayed payloads. These bytes are already decrypted from Host↔Client.
    // We now parse the inner InternalEnvelope and, if it contains a handshake hello, complete responder-side handshake.
    public record ProcessRelayedOpaquePayloadCommand(byte[] OpaquePayload) : IRequest;

    internal class ProcessRelayedOpaquePayloadHandler : IRequestHandler<ProcessRelayedOpaquePayloadCommand>
    {
        private readonly ILogger<ProcessRelayedOpaquePayloadHandler> _logger;
        private readonly IMediator _mediator;
        private readonly IDirectSessionManager _sessions;
        private readonly Percolator.Application.Network.IRatchetKeySessionLookup _ratchetLookup;
        private readonly ActiveIdentityContext _active;

        private static readonly HashSet<InternalEnvelope.ApplicationPayloadOneofCase> AllowedCases = new()
        {
            InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.FileShareEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.DhtEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.PrekeyEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.RelayOpaqueEnvelope,
            InternalEnvelope.ApplicationPayloadOneofCase.SubmitPreKeyBundleResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.EnqueueOpaqueMessageResponse,
            InternalEnvelope.ApplicationPayloadOneofCase.FetchQueuedMessagesResponse
        };

        public ProcessRelayedOpaquePayloadHandler(
            ILogger<ProcessRelayedOpaquePayloadHandler> logger,
            IMediator mediator,
            IDirectSessionManager sessions,
            Percolator.Application.Network.IRatchetKeySessionLookup ratchetLookup,
            ActiveIdentityContext active)
        {
            _logger = logger;
            _mediator = mediator;
            _sessions = sessions;
            _ratchetLookup = ratchetLookup;
            _active = active;
        }

        public async Task Handle(ProcessRelayedOpaquePayloadCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received relayed opaque payload (len={Len})", request.OpaquePayload?.Length ?? 0);

            if (request.OpaquePayload is null || request.OpaquePayload.Length == 0)
            {
                return;
            }
            // The relayed payload is a DR SessionRatchetMessage opaque to the host. Decrypt via fast/slow path to obtain an InternalEnvelope.
            if (_active.Identity is null)
            {
                throw new InvalidOperationException("Active identity not loaded.");
            }

            var ratchetMessage = new SessionRatchetMessage(request.OpaquePayload);
            var header = ratchetMessage.GetHeader();

            // Fast path: resolve session by ratchet header key
            var directSessionId = await _ratchetLookup.TryResolveAsync(header.PreKey, _active.Identity.SelfIdentityId, cancellationToken);

            Plaintext? plaintext;
            SessionId sid;
            if (directSessionId is not null)
            {
                sid = new SessionId(directSessionId.Value.Value);
                plaintext = await _sessions.ReceiveMessageAsync(sid, ratchetMessage);
            }
            else
            {
                // Slow path: infer session and decrypt (may also handle initial pre-key messages)
                var result = await _sessions.TryInferAndReceiveAsync(ratchetMessage, cancellationToken);
                if (result is null)
                {
                    throw new InvalidOperationException("Failed to infer session and decrypt relayed DR message");
                }
                sid = result.Value.sessionId;
                plaintext = result.Value.plaintext;
            }

            if (plaintext is null)
            {
                _logger.LogWarning("Relayed DR message could not be decrypted");
                return;
            }

            InternalEnvelope inner;
            try
            {
                inner = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Decrypted relayed payload was not a valid InternalEnvelope");
                return;
            }

            if (!AllowedCases.Contains(inner.ApplicationPayloadCase))
            {
                _logger.LogWarning("Relayed InternalEnvelope case {Case} not allowed; dropping", inner.ApplicationPayloadCase);
                return;
            }
            _logger.LogDebug("Relayed InternalEnvelope allowed case {Case}; delegating to orchestrator", inner.ApplicationPayloadCase);

            await _mediator.Send(new Percolator.Application.Network.ProcessInternalEnvelopeCommand(
                inner,
                new Percolator.Application.Network.SessionContext(sid.Value, _active.Identity.SelfIdentityId, null)
            ), cancellationToken);
        }
    }
}
