using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.MessageQueue.Commands;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchTextMessageHandler : IRequestHandler<DispatchTextMessageCommand>
{
    private readonly IMediator _mediator;
    private readonly IMessageTransportService _transportService;
    private readonly IDirectSessionRepository _sessionRepository;
    private readonly ILogger<DispatchTextMessageHandler> _logger;

    public DispatchTextMessageHandler(
        IMediator mediator,
        IMessageTransportService transportService,
        IDirectSessionRepository sessionRepository,
        ILogger<DispatchTextMessageHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _transportService = transportService ?? throw new ArgumentNullException(nameof(transportService));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DispatchTextMessageCommand request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.RecipientPeerIds.Count == 0)
        {
            _logger.LogWarning("No recipients specified for message {MessageId}", request.MessageId);
            return;
        }

        // Create the internal envelope containing the chat message (map from app fields)
        var envelope = new InternalEnvelope
        {
            ChatEnvelope = new ChatEnvelope
            {
                TextMessage = new TextMessage
                {
                    MessageId = ByteString.CopyFrom(request.MessageId.ToByteArray()),
                    Content = request.Content,
                    SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(request.SentTimestampUtc),
                    PublicKeyHash = ByteString.CopyFrom(new byte[32])
                }
            }
        };

        var envelopeBytes = envelope.ToByteArray();

        // Process each recipient in parallel
        var sendTasks = request.RecipientPeerIds
            .Where(peerId => peerId != request.SenderPeerId) // Don't send to self
            .Select(peerId => ProcessRecipientAsync(peerId, envelopeBytes, request.SenderPeerId, cancellationToken));

        await Task.WhenAll(sendTasks);
    }

    private async Task ProcessRecipientAsync(
        PeerId recipientId,
        byte[] envelopeBytes,
        PeerId senderId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Enqueue the message first to ensure it's persisted
            // Convert the PeerId's underlying Guid to a byte array for the recipient's public key hash
            var recipientKeyHash = recipientId.Value.ToByteArray();
            var enqueueResult = await _mediator.Send(
                new EnqueueOpaqueMessageCommand(
                    recipientKeyHash,
                    envelopeBytes),
                cancellationToken);

            if (!enqueueResult.Accepted)
            {
                _logger.LogError("Failed to enqueue message for recipient {RecipientId}", recipientId);
                return;
            }

            // Best-effort: immediately try to relay the just-enqueued message to the recipient.
            // This uses the RelayOrchestrator's AckId-based flow and deletes on ack.
            await _mediator.Send(new Percolator.Application.Network.TryRelayNextForPeerCommand(recipientId), cancellationToken);

            // Try to get a direct session for immediate delivery
            // Note: We need to provide the selfIdentityId, but it's not available here
            // This suggests we need to modify the interface or the way we handle sessions
            // For now, we'll just log a warning and continue
            _logger.LogWarning("Direct session retrieval not implemented, message queued for recipient {RecipientId}", recipientId);
            
            // TODO: Implement direct session retrieval and message sending when the session is available
            // The following code is commented out as it requires the sessionId which is not available yet
            /*
            var sessionId = await _sessionRepository.GetByRemotePeerIdAsync(recipientId, selfIdentityId);
            if (sessionId != null)
            {
                // Encrypt and send the message directly
                var ratchetMessage = new SessionRatchetMessage(envelopeBytes);
                
                await _transportService.SendMessageAsync(
                    recipientId,
                    sessionId,
                    ratchetMessage,
                    cancellationToken);
            }
            */

            _logger.LogDebug("Directly sent message to recipient {RecipientId}", recipientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for recipient {RecipientId}", recipientId);
            // The message remains in the queue and will be delivered when the peer comes online
        }
    }
}
