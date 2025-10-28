using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat.Events;
using Percolator.Application.Identity;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers
{
    public class TextMessagePostedHandler : INotificationHandler<TextMessagePostedEvent>
    {
        private readonly IMediator _mediator;
        private readonly ILogger<TextMessagePostedHandler> _logger;
        private readonly ActiveIdentityContext _active;

        public TextMessagePostedHandler(
            IMediator mediator,
            ILogger<TextMessagePostedHandler> logger,
            ActiveIdentityContext active)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _active = active ?? throw new ArgumentNullException(nameof(active));
        }

        public async Task Handle(TextMessagePostedEvent notification, CancellationToken cancellationToken)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            
            if (notification.RecipientPeerIds.Count == 0)
            {
                _logger.LogInformation("No recipients to send message {MessageId} to", notification.MessageId);
                return;
            }

            try
            {
                // Map recipient and sender peer IDs directly (already Guids) and exclude self
                var selfPeerGuid = _active.Identity!.Id;
                var recipientIds = notification.RecipientPeerIds
                    .Where(g => g != selfPeerGuid)
                    .Select(g => new PeerId(g))
                    .ToList();

                // Resolve sender from active identity (must match provided self identity id)
                if (_active.Identity is null || _active.Identity.SelfIdentityId != notification.SenderSelfIdentityId)
                {
                    throw new InvalidOperationException($"Active identity not loaded or mismatched (expected {notification.SenderSelfIdentityId})");
                }
                var senderId = new PeerId(_active.Identity.Id);

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
