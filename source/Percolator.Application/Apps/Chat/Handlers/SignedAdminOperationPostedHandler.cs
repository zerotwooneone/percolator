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
    public sealed class SignedAdminOperationPostedHandler : INotificationHandler<SignedAdminOperationPostedEvent>
    {
        private readonly IMediator _mediator;
        private readonly ILogger<SignedAdminOperationPostedHandler> _logger;

        public SignedAdminOperationPostedHandler(IMediator mediator, ILogger<SignedAdminOperationPostedHandler> logger)
        {
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(SignedAdminOperationPostedEvent notification, CancellationToken cancellationToken)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            if (notification.RecipientIds.Count == 0)
            {
                _logger.LogInformation("No recipients for SignedAdminOperation {OpId}", notification.OpId);
                return;
            }

            var recipientPeerIds = notification.RecipientIds
                .Select(id => new PeerId(new Guid(BitConverter.GetBytes(id))))
                .ToList();
            var senderPeerId = new PeerId(new Guid(BitConverter.GetBytes(notification.SenderId)));

            await _mediator.Send(new DispatchSignedAdminOperationCommand(
                notification.GroupConversationId,
                notification.OpId,
                notification.SentTimestampUtc,
                recipientPeerIds,
                senderPeerId,
                notification.Kind,
                notification.GranteePublicKeySpki,
                notification.MembersToAdd,
                notification.MembersToRemove,
                notification.LeaveGroup,
                notification.NewGroupName,
                notification.NewGroupAvatar,
                notification.Signature,
                notification.AdminSequenceNumber
            ), cancellationToken);
        }
    }
}
