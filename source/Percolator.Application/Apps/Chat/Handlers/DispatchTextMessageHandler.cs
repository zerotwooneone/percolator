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
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchTextMessageHandler : IRequestHandler<DispatchTextMessageCommand>
{
    private readonly IMediator _mediator;
    private readonly IRemoteEnvelopeSender _sender;
    private readonly IPeerPublicSigningKeyStore _keyStore;
    private readonly ILogger<DispatchTextMessageHandler> _logger;

    public DispatchTextMessageHandler(
        IMediator mediator,
        IRemoteEnvelopeSender sender,
        IPeerPublicSigningKeyStore keyStore,
        ILogger<DispatchTextMessageHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
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

        // Build the chat envelope containing the text message
        var chatEnvelope = new ChatEnvelope
        {
            TextMessage = new TextMessage
            {
                MessageId = ByteString.CopyFrom(request.MessageId.ToByteArray()),
                Content = request.Content,
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(request.SentTimestampUtc)
            }
        };

        // Process each recipient sequentially (preserve per-recipient ordering if needed)
        foreach (var peerId in request.RecipientPeerIds.Where(pid => pid != request.SenderPeerId))
        {
            await ProcessRecipientAsync(peerId, chatEnvelope, cancellationToken);
        }
    }

    private async Task ProcessRecipientAsync(
        PeerId recipientId,
        ChatEnvelope chatEnvelope,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve PKH for host-enqueue fallback when no direct session exists
            byte[]? pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientId, cancellationToken);
            var route = new RecipientRoute(recipientId, pkh);
            await _sender.SendChatEnvelopeToPeerAsync(chatEnvelope, route, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for recipient {RecipientId}", recipientId);
            // The sender will have attempted direct or enqueue fallback as available
        }
    }
}
