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
using Percolator.MessageQueue.Primitives;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    // Client-side processor for opaque relayed payloads. These bytes are already decrypted from Host↔Client.
    // We now parse the inner InternalEnvelope and, if it contains a handshake hello, complete responder-side handshake.
    public record ProcessRelayedOpaquePayloadCommand(Payload OpaquePayload) : IRequest<ProcessRelayedOpaquePayloadResponse>;

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
        private readonly IDirectSessionManager _sessions;
        private readonly Percolator.Application.Network.IRatchetKeySessionLookup _ratchetLookup;
        private readonly ActiveIdentityContext _active;
        private readonly IMessageService _messageService;

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
            ActiveIdentityContext active,
            IMessageService messageService)
        {
            _logger = logger;
            _mediator = mediator;
            _sessions = sessions;
            _ratchetLookup = ratchetLookup;
            _active = active;
            _messageService = messageService;
        }

        public async Task<ProcessRelayedOpaquePayloadResponse> Handle(ProcessRelayedOpaquePayloadCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received relayed opaque payload (len={Len})", request.OpaquePayload.Value.Length);

            if (request.OpaquePayload is null || request.OpaquePayload.Value.Length == 0)
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            // First attempt: treat as a DR SessionRatchetMessage opaque to the host.
            if (_active.Identity is null)
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
                // Not a valid ratchet message; attempt plaintext HandshakeInitiatorHello fallback
                return await TryHandlePlaintextHelloAsync(request.OpaquePayload, cancellationToken).ConfigureAwait(false);
            }

            (RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) header;
            try
            {
                header = ratchetMessage.GetHeader();
            }
            catch (Exception drEx)
            {
                // Not a valid ratchet message; attempt plaintext HandshakeInitiatorHello fallback
                var hello = HandshakeInitiatorHello.Parser.ParseFrom(request.OpaquePayload.Value);
                if (hello is null || !hello.HasInitiatorIdentityKeySpki || !hello.HasInitiatorEphemeralKeySpki ||
                    !hello.HasSignedPreKeyId)
                {
                    _logger.LogError(drEx,
                        "Error processing opaque message (DR path), and payload was not a valid HandshakeInitiatorHello");
                    throw;
                }

                var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
                var eph = hello.InitiatorEphemeralKeySpki.ToByteArray();
                var spkId = new Guid(hello.SignedPreKeyId.ToByteArray());
                Guid? otkId = hello.HasOneTimePreKeyId ? new Guid(hello.OneTimePreKeyId.ToByteArray()) : (Guid?)null;

                var hsResult = await _mediator.Send(
                    new Percolator.Application.Network.Handshake.HandleHandshakeInitiatorHelloCommand(
                        spki,
                        eph,
                        spkId,
                        otkId,
                        null,
                        hello.HasEncryptedPayload ? hello.EncryptedPayload.ToByteArray() : null),
                    cancellationToken).ConfigureAwait(false);

                if (hsResult is null)
                {
                    return ProcessRelayedOpaquePayloadResponse.Failure;
                }
                // Send the pre-encrypted responder hello to the initiator using direct-first, relay-fallback
                var send = await _messageService.SendPreEncryptedAsync(hsResult.RemotePeerId, hsResult.Cipher, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Handshake responder message sent via {Path}", send.Path);

                return ProcessRelayedOpaquePayloadResponse.Success;
            }

            // Fast path: resolve session by ratchet header key
            var directSessionId = await _ratchetLookup.TryResolveAsync(header.PreKey, _active.Identity.SelfIdentityId, cancellationToken).ConfigureAwait(false);

            Plaintext? plaintext;
            SessionId sid;
            if (directSessionId is not null)
            {
                sid = new SessionId(directSessionId.Value.Value);
                plaintext = await _sessions.ReceiveMessageAsync(sid, ratchetMessage).ConfigureAwait(false);
            }
            else
            {
                // Slow path: infer session and decrypt (may also handle initial pre-key messages)
                var result = await _sessions.TryInferAndReceiveAsync(ratchetMessage, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    // Fallback: raw payload might be a plaintext HandshakeInitiatorHello
                    return await TryHandlePlaintextHelloAsync(request.OpaquePayload, cancellationToken).ConfigureAwait(false);
                }
                sid = result.Value.sessionId;
                plaintext = result.Value.plaintext;
            }

            if (plaintext is null)
            {
                _logger.LogWarning("Relayed DR message could not be decrypted; attempting plaintext HandshakeInitiatorHello fallback");
                return await TryHandlePlaintextHelloAsync(request.OpaquePayload, cancellationToken).ConfigureAwait(false);
            }

            InternalEnvelope inner;
            try
            {
                inner = InternalEnvelope.Parser.ParseFrom(plaintext.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Decrypted relayed payload was not a valid InternalEnvelope; attempting plaintext HandshakeInitiatorHello fallback");
                return await TryHandlePlaintextHelloAsync(request.OpaquePayload, cancellationToken).ConfigureAwait(false);
            }

            if (!AllowedCases.Contains(inner.ApplicationPayloadCase))
            {
                _logger.LogWarning("Relayed InternalEnvelope case {Case} not allowed; dropping", inner.ApplicationPayloadCase);
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }
            _logger.LogDebug("Relayed InternalEnvelope allowed case {Case}; delegating to orchestrator", inner.ApplicationPayloadCase);

            await _mediator.Send(new Percolator.Application.Network.ProcessInternalEnvelopeCommand(
                inner,
                new Percolator.Application.Network.SessionContext(sid.Value, _active.Identity.SelfIdentityId, null)
            ), cancellationToken).ConfigureAwait(false);

            return ProcessRelayedOpaquePayloadResponse.Success;
        }

        private async Task<ProcessRelayedOpaquePayloadResponse> TryHandlePlaintextHelloAsync(Payload payload, CancellationToken cancellationToken)
        {
            try
            {
                var hello = HandshakeInitiatorHello.Parser.ParseFrom(payload.Value);
                if (hello is not null && hello.HasInitiatorIdentityKeySpki && hello.HasInitiatorEphemeralKeySpki && hello.HasSignedPreKeyId)
                {
                    var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
                    var eph = hello.InitiatorEphemeralKeySpki.ToByteArray();
                    var spkId = new Guid(hello.SignedPreKeyId.ToByteArray());
                    Guid? otkId = hello.HasOneTimePreKeyId ? new Guid(hello.OneTimePreKeyId.ToByteArray()) : (Guid?)null;

                    var hsResult = await _mediator.Send(
                        new HandleHandshakeInitiatorHelloCommand(
                            spki,
                            eph,
                            spkId,
                            otkId,
                            null,
                            hello.HasEncryptedPayload ? hello.EncryptedPayload.ToByteArray() : null),
                        cancellationToken).ConfigureAwait(false);
                    if (hsResult is null)
                    {
                        return ProcessRelayedOpaquePayloadResponse.Failure;
                    }
                    var send = await _messageService.SendPreEncryptedAsync(hsResult.RemotePeerId, hsResult.Cipher, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Handshake responder message sent via {Path}", send.Path);
                }
            }
            catch
            {
                return ProcessRelayedOpaquePayloadResponse.Failure;
            }

            return ProcessRelayedOpaquePayloadResponse.Success;
        }
    }

    
}
