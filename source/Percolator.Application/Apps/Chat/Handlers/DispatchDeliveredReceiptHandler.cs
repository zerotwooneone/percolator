using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.MessageQueue.Commands;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchDeliveredReceiptHandler : IRequestHandler<DispatchDeliveredReceiptCommand>
{
    private readonly IMediator _mediator;
    private readonly ILogger<DispatchDeliveredReceiptHandler> _logger;

    public DispatchDeliveredReceiptHandler(IMediator mediator, ILogger<DispatchDeliveredReceiptHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
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

        var envelope = new InternalEnvelope
        {
            ChatEnvelope = new ChatEnvelope
            {
                DeliveredReceipt = new DeliveredReceipt
                {
                    MessageId = ByteString.CopyFrom(request.MessageId.ToByteArray()),
                    SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(request.SentTimestampUtc),
                    PublicKeyHash = ByteString.CopyFrom(new byte[32])
                }
            }
        };
        var envelopeBytes = envelope.ToByteArray();

        var tasks = request.RecipientPeerIds
            .Where(pid => pid != request.SenderPeerId)
            .Select(pid => ProcessRecipientAsync(pid, envelopeBytes, cancellationToken));

        await Task.WhenAll(tasks);
    }

    private async Task ProcessRecipientAsync(PeerId recipientId, byte[] envelopeBytes, CancellationToken cancellationToken)
    {
        try
        {
            var recipientKeyHash = recipientId.Value.ToByteArray();
            var enqueueResult = await _mediator.Send(
                new EnqueueOpaqueMessageCommand(
                    recipientKeyHash,
                    envelopeBytes),
                cancellationToken);

            if (!enqueueResult.Accepted)
            {
                _logger.LogError("Failed to enqueue delivered receipt for recipient {RecipientId}", recipientId);
                return;
            }

            await _mediator.Send(new Percolator.Application.Network.TryRelayNextForPeerCommand(recipientId), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing delivered receipt for recipient {RecipientId}", recipientId);
        }
    }
}
