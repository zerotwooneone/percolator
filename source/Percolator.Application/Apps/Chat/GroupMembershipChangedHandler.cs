using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Chat.App;
using Percolator.Chat;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using ChatMembershipChanged = Percolator.Chat.App.GroupMembershipChangedNotification;

namespace Percolator.Application.Apps.Chat
{
    // Reacts to membership changes by preparing distribution of a new group key version (acting-admin path)
    internal sealed class GroupMembershipChangedHandler : INotificationHandler<ChatMembershipChanged>
    {
        private readonly ILogger<GroupMembershipChangedHandler> _logger;
        private readonly IConversationRepository _conversationRepository;
        private readonly ISelfParticipantIdProvider _selfProvider;
        private readonly IMediator _mediator;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IGroupAdminStateStore _adminStateStore;
        private readonly IGroupManagerStateStore _gmStateStore;
        private readonly IAtRestKeyProvider _atRestKeyProvider;
        private readonly IRecipientPkhResolver _recipientPkhResolver;
        private readonly IRemoteEnvelopeSender _sender;

        public GroupMembershipChangedHandler(
            ILogger<GroupMembershipChangedHandler> logger,
            IConversationRepository conversationRepository,
            ISelfParticipantIdProvider selfProvider,
            IMediator mediator,
            ActiveIdentityContext activeIdentityContext,
            IGroupAdminStateStore adminStateStore,
            IGroupManagerStateStore gmStateStore,
            IAtRestKeyProvider atRestKeyProvider,
            IRecipientPkhResolver recipientPkhResolver,
            IRemoteEnvelopeSender sender)
        {
            _logger = logger;
            _conversationRepository = conversationRepository;
            _selfProvider = selfProvider;
            _mediator = mediator;
            _activeIdentityContext = activeIdentityContext;
            _adminStateStore = adminStateStore;
            _gmStateStore = gmStateStore;
            _atRestKeyProvider = atRestKeyProvider;
            _recipientPkhResolver = recipientPkhResolver;
            _sender = sender;
        }

        public async Task Handle(ChatMembershipChanged notification, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Group membership change cutover pending (Step 8): replace legacy GroupManager/transport key flow");
        }
    }
}
