using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat.Events;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers
{
    public class TextMessagePostedHandler : INotificationHandler<TextMessagePostedEvent>
    {
        private readonly IMediator _mediator;
        private readonly ILogger<TextMessagePostedHandler> _logger;

        public TextMessagePostedHandler(
            IMediator mediator,
            ILogger<TextMessagePostedHandler> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(TextMessagePostedEvent notification, CancellationToken cancellationToken)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            
            if (notification.RecipientIds.Count == 0)
            {
                _logger.LogInformation("No recipients to send message {MessageId} to", notification.MessageId);
                return;
            }

            try
            {
                // Convert recipient IDs from long to PeerId (Guid)
                var recipientIds = notification.RecipientIds
                    .Select(id => new PeerId(new Guid(BitConverter.GetBytes(id))))
                    .ToList();

                // Convert sender ID from long to PeerId (Guid)
                var senderId = new PeerId(new Guid(BitConverter.GetBytes(notification.SenderId)));

                // Send the command with app-level fields (no Contracts dependency)
                await _mediator.Send(
                    new DispatchTextMessageCommand(
                        notification.MessageId,
                        notification.Content,
                        notification.SentTimestampUtc,
                        recipientIds,
                        senderId),
                    cancellationToken);

                _logger.LogInformation("Dispatched message {MessageId} to {RecipientCount} recipients",
                    notification.MessageId, recipientIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching message {MessageId}", notification.MessageId);
                throw;
            }
        }
    }
}
