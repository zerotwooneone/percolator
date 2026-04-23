using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using Percolator.Contracts;
using PeerId = Percolator.Identity.PeerId;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

public sealed class DispatchReadReceiptHandler : IRequestHandler<DispatchReadReceiptCommand>
{
    private readonly IMediator _mediator;
    private readonly IRemoteEnvelopeSender _sender;
    private readonly IPeerPublicSigningKeyStore _keyStore;
    private readonly ILogger<DispatchReadReceiptHandler> _logger;

    public DispatchReadReceiptHandler(
        IMediator mediator,
        IRemoteEnvelopeSender sender,
        IPeerPublicSigningKeyStore keyStore,
        ILogger<DispatchReadReceiptHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DispatchReadReceiptCommand request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.RecipientPeerIds.Count == 0)
        {
            _logger.LogWarning("No recipients specified for read receipt {MessageId}", request.MessageId);
            return;
        }

        var chat = new ChatEnvelope
        {
            ReadReceipt = new ReadReceipt
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
            IdentityPublicKeyHash? pkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientId, cancellationToken).ConfigureAwait(false);
            await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing read receipt for recipient {RecipientId}", recipientId);
        }
    }
}
