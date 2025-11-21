using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Application.Network;

namespace Percolator.Application.ReverseSignal
{
    public class ReverseSignalAcceptService
    {
        private readonly ILogger<ReverseSignalAcceptService> _logger;
        private readonly IPendingSessionRepository _pending;
        private readonly IMediator _mediator;
        private readonly IMessageService _messageService;

        public ReverseSignalAcceptService(
            ILogger<ReverseSignalAcceptService> logger,
            IPendingSessionRepository pending,
            IMediator mediator,
            IMessageService messageService)
        {
            _logger = logger;
            _pending = pending;
            _mediator = mediator;
            _messageService = messageService;
        }

        public async Task<bool> AcceptAsync(PendingSessionId id, CancellationToken cancellationToken = default)
        {
            var pending = await _pending.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                _logger.LogWarning("No pending session found for {Id}", id.Value);
                return false;
            }

            // Defensive expiry check
            if (pending.ExpiresAtUtc is not null && pending.ExpiresAtUtc.Value <= DateTimeOffset.UtcNow)
            {
                _logger.LogInformation("Pending session {Id} expired; deleting", id.Value);
                await _pending.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                return false;
            }

            // Parse invitation as HandshakeInitiatorHello
            HandshakeInitiatorHello hello;
            try
            {
                hello = HandshakeInitiatorHello.Parser.ParseFrom(pending.Invitation.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pending session {Id} invitation bytes were not a valid HandshakeInitiatorHello", id.Value);
                return false;
            }

            if (!hello.HasInitiatorIdentityKeySpki || !hello.HasInitiatorEphemeralKeySpki || !hello.HasSignedPreKeyId)
            {
                _logger.LogWarning("Pending session {Id} invitation missing required fields", id.Value);
                return false;
            }

            var spki = hello.InitiatorIdentityKeySpki.ToByteArray();
            var eph = hello.InitiatorEphemeralKeySpki.ToByteArray();
            var spkId = new Guid(hello.SignedPreKeyId.ToByteArray());
            Guid? otkId = hello.HasOneTimePreKeyId ? new Guid(hello.OneTimePreKeyId.ToByteArray()) : (Guid?)null;

            // Use existing responder handler to complete X3DH/DR and build the responder hello cipher
            var result = await _mediator.Send(new HandleHandshakeInitiatorHelloCommand(
                spki,
                eph,
                spkId,
                otkId,
                null,
                hello.HasEncryptedPayload ? hello.EncryptedPayload.ToByteArray() : null,
                RelayHostPeerId: null
            ), cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                _logger.LogWarning("Failed to handle initiator hello for pending session {Id}", id.Value);
                return false;
            }

            // Send the pre-encrypted responder hello to the initiator using direct-first, relay-fallback
            var send = await _messageService.SendPreEncryptedAsync(result.RemotePeerId, result.Cipher, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Reverse-signal accept: responder hello attempted via {Path}", send.Path);
            if (send.Success)
            {
                await _pending.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Accepted and removed pending session {Id}", id.Value);
                return true;
            }
            _logger.LogWarning("Failed to deliver responder hello for pending session {Id}; keeping pending", id.Value);
            return false;
        }
    }
}
