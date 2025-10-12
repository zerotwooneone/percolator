using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.MessageQueue.Commands;

namespace Percolator.Application.Network.Handshake
{
    // Initiator-side: compose a HandshakeInitiatorHello and enqueue it via MQ to recipient PKH.
    public record ComposeAndEnqueueInitiatorHelloCommand(
        byte[] RecipientPublicKeyHash,
        byte[] RemoteIdentityKeySpki,
        Guid SignedPreKeyId,
        Guid? OneTimePreKeyId,
        byte[] RemotePreKeySpki,
        byte[]? InitiatorPayload = null
    ) : IRequest;

    internal class ComposeAndEnqueueInitiatorHelloHandler : IRequestHandler<ComposeAndEnqueueInitiatorHelloCommand>
    {
        private readonly ILogger<ComposeAndEnqueueInitiatorHelloHandler> _logger;
        private readonly ActiveIdentityContext _active;
        private readonly IX3DHOrchestrator _x3dh;
        private readonly IMediator _mediator;
        private readonly IDirectSessionManager _sessions;

        public ComposeAndEnqueueInitiatorHelloHandler(
            ILogger<ComposeAndEnqueueInitiatorHelloHandler> logger,
            ActiveIdentityContext active,
            IX3DHOrchestrator x3dh,
            IMediator mediator,
            IDirectSessionManager sessions)
        {
            _logger = logger;
            _active = active;
            _x3dh = x3dh;
            _mediator = mediator;
            _sessions = sessions;
        }

        public async Task Handle(ComposeAndEnqueueInitiatorHelloCommand request, CancellationToken cancellationToken)
        {
            if (_active.Keys is null)
            {
                throw new InvalidOperationException("Active identity keys not loaded.");
            }

            // Build remote bundle used for X3DH initiation
            var remoteId = new RatchetIdentityKey(request.RemoteIdentityKeySpki);
            
            // Generate ephemeral and initiate X3DH
            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var remotePreKey = new PreKey(request.RemotePreKeySpki);
            var otk = request.OneTimePreKeyId.HasValue ? new OneTimeKey(Array.Empty<byte>()) : null; // OTK public key not required in hello
            var bundle = new X3dPreKeyBundle(remoteId, remotePreKey, otk);
            var shared = _x3dh.InitiateHandshake(bundle, eph);
                
            _logger.LogInformation("Established new initiator prehandshake for remote identity {RemoteIdentityKey}", 
                Convert.ToBase64String(remoteId.Value));

            // Persist initiator intent (Pending) via session manager (no session id yet)
            // If InitiatorPayload is present, request a temporary encryption and embed as EncryptedPayload.
            var initialPlaintext = request.InitiatorPayload is { Length: > 0 } ? new Plaintext(request.InitiatorPayload) : null;
            var ratchetMessage = await _sessions.EstablishSessionAsInitiatorAsync(
                request.RecipientPublicKeyHash,
                request.SignedPreKeyId,
                request.OneTimePreKeyId,
                remoteId,
                remotePreKey,
                shared,
                eph,
                initialPlaintext,
                cancellationToken);

            // Compose initiator hello
            var hello = new HandshakeInitiatorHello
            {
                Version = 1,
                InitiatorIdentityKeySpki = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                InitiatorEphemeralKeySpki = ByteString.CopyFrom(eph.PublicKey.ExportSubjectPublicKeyInfo()),
                SignedPreKeyId = ByteString.CopyFrom(request.SignedPreKeyId.ToByteArray()),
            };
            if (request.OneTimePreKeyId.HasValue)
            {
                hello.OneTimePreKeyId = ByteString.CopyFrom(request.OneTimePreKeyId.Value.ToByteArray());
            }

            // Optional encrypted payload carrying the first opaque message
            if (ratchetMessage is not null)
            {
                hello.EncryptedPayload = ByteString.CopyFrom(ratchetMessage.Value);
            }

            var env = new InternalEnvelope { HandshakeInitiatorHello = hello };
            var blob = env.ToByteArray();

            // Enqueue via MQ to recipient PKH
            await _mediator.Send(new EnqueueOpaqueMessageCommand(request.RecipientPublicKeyHash, blob), cancellationToken);

            _logger.LogInformation("Enqueued HandshakeInitiatorHello to recipient PKH");
        }
    }
}
