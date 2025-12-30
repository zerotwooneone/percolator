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
using Percolator.Application.Services;
using Percolator.MessageQueue.Primitives;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    // Client-side processor for opaque relayed payloads. These bytes are already decrypted from Host↔Client.
    // We now parse the inner InternalEnvelope and, if it contains a handshake hello, complete responder-side handshake.
    public record ProcessRelayedOpaquePayloadCommand(Payload OpaquePayload, Percolator.Identity.PeerId RelayHostPeerId) : IRequest<ProcessRelayedOpaquePayloadResponse>;

    internal record ProcessRelayedOpaquePayloadResponse
    {
        public bool WasSuccess { get; private init; }
        public static ProcessRelayedOpaquePayloadResponse Success => new() { WasSuccess = true };
        public static ProcessRelayedOpaquePayloadResponse Failure => new() { WasSuccess = false };  
    };
    internal class ProcessRelayedOpaquePayloadHandler : IRequestHandler<ProcessRelayedOpaquePayloadCommand, ProcessRelayedOpaquePayloadResponse>
    {
        private readonly ILogger<ProcessRelayedOpaquePayloadHandler> _logger;
        private readonly IMediator _mediator;
        private readonly ISecureMessagingService _secureMessaging;
        private readonly IRatchetKeyIndex _ratchetLookup;
        private readonly IActiveIdentityAccessor _activeIdentityAccessor;
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
            ISecureMessagingService secureMessaging,
            IRatchetKeyIndex ratchetLookup,
            IActiveIdentityAccessor activeIdentityAccessor,
            ActiveIdentityContext active)
        {
            _logger = logger;
            _mediator = mediator;
            _secureMessaging = secureMessaging;
            _ratchetLookup = ratchetLookup;
            _activeIdentityAccessor = activeIdentityAccessor;
            _active = active;
        }

        public async Task<ProcessRelayedOpaquePayloadResponse> Handle(ProcessRelayedOpaquePayloadCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received relayed opaque payload (len={Len})", request.OpaquePayload.Value.Length);

            if (request.OpaquePayload is null || request.OpaquePayload.Value.Length == 0)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            // First attempt: treat as a DR SessionRatchetMessage opaque to the host.
            if (!_activeIdentityAccessor.IsActive || _active.Identity is null)
            {
                throw new InvalidOperationException("Active identity not loaded.");
            }

            SessionRatchetMessage ratchetMessage;
            try
            {
                ratchetMessage = new SessionRatchetMessage(request.OpaquePayload.Value);
            }
            catch
            {
                // Not a valid ratchet message
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            (RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) header;
            try
            {
                header = ratchetMessage.GetHeader();
            }
            catch (Exception drEx)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            // Fast/slow path via SecureMessagingService
            var resolved = await _secureMessaging.DecryptInboundAsync(ratchetMessage, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            var sid = resolved.Value.sessionId;
            var plaintext = resolved.Value.plaintext;

            if (plaintext is null)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            InternalEnvelope inner;
            try
            {
                inner = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Decrypted relayed payload was not a valid InternalEnvelope; dropping");
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            if (!AllowedCases.Contains(inner.ApplicationPayloadCase))
            {
                _logger.LogWarning("Relayed InternalEnvelope case {Case} not allowed; dropping", inner.ApplicationPayloadCase);
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            _logger.LogDebug("Relayed InternalEnvelope allowed case {Case}; delegating to orchestrator", inner.ApplicationPayloadCase);

            await _mediator.Send(new Percolator.Application.Network.ProcessInternalEnvelopeCommand(
                inner,
                new Percolator.Application.Network.SessionContext(sid.Value, _active.Identity.SelfIdentityId.Value, null)
            ), cancellationToken).ConfigureAwait(false);

            return ProcessRelayedOpaquePayloadResponse.Success;
        }
    }

    
}
