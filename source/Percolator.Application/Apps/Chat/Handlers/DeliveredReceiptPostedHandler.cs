using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat.Messaging.Events;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers
{
    public sealed class DeliveredReceiptPostedHandler : INotificationHandler<DeliveredReceiptPostedEvent>
    {
        private readonly IMediator _mediator;
        private readonly ILogger<DeliveredReceiptPostedHandler> _logger;

        public DeliveredReceiptPostedHandler(IMediator mediator, ILogger<DeliveredReceiptPostedHandler> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(DeliveredReceiptPostedEvent notification, CancellationToken cancellationToken)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            if (notification.RecipientIds.Count == 0)
            {
                _logger.LogInformation("No recipients to send delivered receipt for message {MessageId}", notification.MessageId);
                return;
            }

            var recipientPeerIds = notification.RecipientIds
                .Select(id => new PeerId(new Guid(BitConverter.GetBytes(id))))
                .ToList();
            var senderPeerId = new PeerId(new Guid(BitConverter.GetBytes(notification.SenderId)));

            await _mediator.Send(new DispatchDeliveredReceiptCommand(
                notification.MessageId,
                notification.SentTimestampUtc,
                recipientPeerIds,
                senderPeerId
            ), cancellationToken).ConfigureAwait(false);
        }
    }
}
