using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat.Events;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers
{
    public sealed class EmojiAnnotationPostedHandler : INotificationHandler<EmojiAnnotationPostedEvent>
    {
        private readonly IMediator _mediator;
        private readonly ILogger<EmojiAnnotationPostedHandler> _logger;

        public EmojiAnnotationPostedHandler(IMediator mediator, ILogger<EmojiAnnotationPostedHandler> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(EmojiAnnotationPostedEvent notification, CancellationToken cancellationToken)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            if (notification.RecipientIds.Count == 0)
            {
                _logger.LogInformation("No recipients to send emoji annotation for message {MessageId}", notification.MessageId);
                return;
            }

            var recipientPeerIds = notification.RecipientIds
                .Select(id => new PeerId(new Guid(BitConverter.GetBytes(id))))
                .ToList();
            var senderPeerId = new PeerId(new Guid(BitConverter.GetBytes(notification.SenderId)));

            await _mediator.Send(new DispatchEmojiAnnotationCommand(
                notification.MessageId,
                notification.Emoji,
                notification.SentTimestampUtc,
                recipientPeerIds,
                senderPeerId
            ), cancellationToken).ConfigureAwait(false);
        }
    }
}
