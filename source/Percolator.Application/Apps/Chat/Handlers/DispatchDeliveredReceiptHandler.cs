using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Apps.Chat.Commands;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Identity;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class DispatchDeliveredReceiptHandler : IRequestHandler<DispatchDeliveredReceiptCommand>
{
    private readonly IMediator _mediator;
    private readonly IRemoteEnvelopeSender _sender;
    private readonly IPeerPublicSigningKeyStore _keyStore;
    private readonly ILogger<DispatchDeliveredReceiptHandler> _logger;

    public DispatchDeliveredReceiptHandler(IMediator mediator, IRemoteEnvelopeSender sender, IPeerPublicSigningKeyStore keyStore, ILogger<DispatchDeliveredReceiptHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DispatchDeliveredReceiptCommand request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.RecipientPeerIds.Count == 0)
        {
            _logger.LogWarning("No recipients specified for delivered receipt {MessageId}", request.MessageId);
            return;
        }

        var chat = new ChatEnvelope
        {
            DeliveredReceipt = new DeliveredReceipt
            {
                MessageId = ByteString.CopyFrom(request.MessageId.ToByteArray()),
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(request.SentTimestampUtc)
            }
        };

        var tasks = request.RecipientPeerIds
            .Where(pid => pid != request.SenderPeerId)
            .Select(pid => ProcessRecipientAsync(pid, chat, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ProcessRecipientAsync(PeerId recipientId, ChatEnvelope chat, CancellationToken cancellationToken)
    {
        try
        {
            await _sender.SendChatEnvelopeToPeerAsync(chat, recipientId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing delivered receipt for recipient {RecipientId}", recipientId);
        }
    }
}
