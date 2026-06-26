using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Apps.Chat.Commands;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Identity;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class DispatchEmojiAnnotationHandler : IRequestHandler<DispatchEmojiAnnotationCommand>
{
    private readonly IMediator _mediator;
    private readonly IRemoteEnvelopeSender _sender;
    private readonly IPeerPublicSigningKeyStore _keyStore;
    private readonly ILogger<DispatchEmojiAnnotationHandler> _logger;

    public DispatchEmojiAnnotationHandler(IMediator mediator, IRemoteEnvelopeSender sender, IPeerPublicSigningKeyStore keyStore, ILogger<DispatchEmojiAnnotationHandler> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DispatchEmojiAnnotationCommand request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.RecipientPeerIds.Count == 0)
        {
            _logger.LogWarning("No recipients specified for emoji annotation {MessageId}", request.MessageId);
            return;
        }

        var chat = new ChatEnvelope
        {
            EmojiAnnotation = new EmojiAnnotation
            {
                MessageId = ByteString.CopyFrom(request.MessageId.ToByteArray()),
                Emoji = request.Emoji,
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
            await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing emoji annotation for recipient {RecipientId}", recipientId);
        }
    }
}
