using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.ValueObjects;
using Percolator.Application.Identity;

namespace Percolator.Application.Apps.Chat
{
    // Observes persisted key adoption confirmations and logs progress toward readiness for commit broadcast.
    // Future: trigger SignedAdminCommitOperation when policy threshold is met.
    internal sealed class KeyAdoptionStoredHandler : INotificationHandler<KeyAdoptionStoredNotification>
    {
        private readonly ILogger<KeyAdoptionStoredHandler> _logger;
        private readonly IKeyAdoptionStore _adoptions;
        private readonly IConversationRepository _conversations;
        private readonly ActiveIdentityContext _activeIdentityContext;

        public KeyAdoptionStoredHandler(ILogger<KeyAdoptionStoredHandler> logger, IKeyAdoptionStore adoptions, IConversationRepository conversations, ActiveIdentityContext activeIdentityContext)
        {
            _logger = logger;
            _adoptions = adoptions;
            _conversations = conversations;
            _activeIdentityContext = activeIdentityContext;
        }

        public async Task Handle(KeyAdoptionStoredNotification notification, CancellationToken cancellationToken)
        {
            var selfId = _activeIdentityContext.Identity.SelfIdentityId;
            var conversation = await _conversations.GetByIdAsync(new ConversationId(notification.ConversationId), selfId.Value).ConfigureAwait(false);
            if (conversation is null)
            {
                _logger.LogWarning("[KeyAdoptionStored] Conversation {ConversationId} not found.", notification.ConversationId);
                return;
            }

            var count = await _adoptions.GetCountAsync(notification.ConversationId, notification.Version, cancellationToken).ConfigureAwait(false);
            var participantCount = conversation.Participants.Count;
            _logger.LogInformation("[KeyAdoptionStored] Conversation {ConversationId} key v{Version}: {Count}/{Total} adoptions recorded.", notification.ConversationId, notification.Version.Value, count, participantCount);

            // Policy deferred: commit trigger to be implemented later when acting-admin commit flow is wired.
        }
    }
}
