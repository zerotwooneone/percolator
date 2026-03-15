using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat.Events;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Apps.Chat.Handlers
{
    public sealed class SignedAdminCommitOperationPostedHandler : INotificationHandler<SignedAdminCommitOperationPostedEvent>
    {
        private readonly IMediator _mediator;
        private readonly ILogger<SignedAdminCommitOperationPostedHandler> _logger;

        public SignedAdminCommitOperationPostedHandler(IMediator mediator, ILogger<SignedAdminCommitOperationPostedHandler> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(SignedAdminCommitOperationPostedEvent notification, CancellationToken cancellationToken)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            if (notification.RecipientIds.Count == 0)
            {
                _logger.LogInformation("No recipients for SignedAdminCommitOperation {OpId}", notification.OpId);
                return;
            }

            var recipientPeerIds = notification.RecipientIds
                .Select(id => new PeerId(new Guid(BitConverter.GetBytes(id))))
                .ToList();
            var senderPeerId = new PeerId(new Guid(BitConverter.GetBytes(notification.SenderId)));

            await _mediator.Send(new DispatchSignedAdminCommitOperationCommand(
                notification.GroupConversationId,
                notification.OpId,
                notification.CommittedKeyVersion,
                notification.SentTimestampUtc,
                recipientPeerIds,
                senderPeerId,
                notification.Signature,
                notification.AdminSequenceNumber
            ), cancellationToken).ConfigureAwait(false);
        }
    }
}
