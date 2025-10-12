using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;

namespace Percolator.Application.Network.Handshake
{
    // Client-side processor for opaque relayed payloads. These bytes are already decrypted from Host↔Client.
    // We now parse the inner InternalEnvelope and, if it contains a handshake hello, complete responder-side handshake.
    public record ProcessRelayedOpaquePayloadCommand(byte[] OpaquePayload) : IRequest;

    internal class ProcessRelayedOpaquePayloadHandler : IRequestHandler<ProcessRelayedOpaquePayloadCommand>
    {
        private readonly ILogger<ProcessRelayedOpaquePayloadHandler> _logger;
        private readonly IMediator _mediator;
        private readonly IPeerPublicSigningKeyStore _pkhStore;

        public ProcessRelayedOpaquePayloadHandler(
            ILogger<ProcessRelayedOpaquePayloadHandler> logger,
            IMediator mediator,
            IPeerPublicSigningKeyStore pkhStore)
        {
            _logger = logger;
            _mediator = mediator;
            _pkhStore = pkhStore;
        }

        public async Task Handle(ProcessRelayedOpaquePayloadCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Received relayed opaque payload (len={Len})", request.OpaquePayload?.Length ?? 0);

            if (request.OpaquePayload is null || request.OpaquePayload.Length == 0)
            {
                return;
            }

            InternalEnvelope inner;
            try
            {
                inner = InternalEnvelope.Parser.ParseFrom(request.OpaquePayload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse inner InternalEnvelope from relayed payload");
                return;
            }

            switch (inner.ApplicationPayloadCase)
            {
                case InternalEnvelope.ApplicationPayloadOneofCase.HandshakeInitiatorHello:
                {
                    var hello = inner.HandshakeInitiatorHello;
                    // minimally validate proto has required fields
                    if (!hello.HasInitiatorIdentityKeySpki || !hello.HasInitiatorEphemeralKeySpki || !hello.HasSignedPreKeyId)
                    {
                        _logger.LogWarning("Relayed HandshakeInitiatorHello missing required fields");
                        return;
                    }

                    // Resolve optional peer id via PKH if available
                    PeerId? remotePeerId = null;
                    try
                    {
                        var pkh = System.Security.Cryptography.SHA256.HashData(hello.InitiatorIdentityKeySpki.ToByteArray());
                        remotePeerId = await _pkhStore.GetPeerIdByPublicKeyHashAsync(pkh, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "PKH lookup failed for relayed hello (non-fatal)");
                    }

                    var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
                    var eph = hello.InitiatorEphemeralKeySpki.ToByteArray();
                    var spkId = new Guid(hello.SignedPreKeyId.ToByteArray());
                    Guid? otkId = hello.HasOneTimePreKeyId ? new Guid(hello.OneTimePreKeyId.ToByteArray()) : (Guid?)null;

                    // Delegate to dedicated handler to establish session and build responder hello
                    var responderEnvelope = await _mediator.Send(new HandleHandshakeInitiatorHelloCommand(spki, eph, spkId, otkId, remotePeerId), cancellationToken);
                    if (responderEnvelope is not null)
                    {
                        // Enqueue responder hello back to the initiator via host using MQ (durable)
                        var recipientPkh = System.Security.Cryptography.SHA256.HashData(spki);
                        var blob = responderEnvelope.ToByteArray();
                        await _mediator.Send(new EnqueueOpaqueMessageCommand(recipientPkh, blob), cancellationToken);
                    }
                    return;
                }
                case InternalEnvelope.ApplicationPayloadOneofCase.HandshakeResponderHello:
                {
                    var hello = inner.HandshakeResponderHello;
                    // Only EncryptedPayload is required; session identification will be inferred via ratchet header.
                    if (!hello.HasEncryptedPayload || hello.EncryptedPayload.IsEmpty)
                    {
                        _logger.LogWarning("Relayed HandshakeResponderHello missing required fields");
                        return;
                    }

                    await _mediator.Send(new HandleHandshakeResponderHelloCommand(
                        hello.EncryptedPayload.ToByteArray()
                    ), cancellationToken);
                    return;
                }
                default:
                    _logger.LogWarning("Relayed payload has unknown payload case. type: {Type}", inner.ApplicationPayloadCase);
                    return;
            }
        }
    }
}
