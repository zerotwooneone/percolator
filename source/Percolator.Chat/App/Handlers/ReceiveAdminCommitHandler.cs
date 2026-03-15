using MediatR;
using Percolator.Chat.App.Commands;

namespace Percolator.Chat.App.Handlers
{
    // Applies first-commit-wins using admin_sequence_number and enforces key version continuity.
    public class ReceiveAdminCommitHandler : IRequestHandler<ReceiveAdminCommitCommand>
    {
        private readonly IConversationResolver _resolver;
        private readonly IGroupAdminStateStore _stateStore;

        public ReceiveAdminCommitHandler(IConversationResolver resolver, IGroupAdminStateStore stateStore)
        {
            _resolver = resolver;
            _stateStore = stateStore;
        }

        public async Task Handle(ReceiveAdminCommitCommand request, CancellationToken cancellationToken)
        {
            // Resolve to ensure conversation exists locally
            request.Lookup.EnsureExactlyOne();
            var resolution = await _resolver.ResolveAsync(request.Lookup, cancellationToken);
            var conversationId = resolution.Conversation.Id.Value;

            // Initialize state if missing
            await _stateStore.InitializeIfMissingAsync(conversationId, cancellationToken);

            // First-commit-wins + key-version continuity
            var committed = await _stateStore.TryCommitAsync(
                conversationId,
                request.AdminSequenceNumber,
                request.CommittedKeyVersion.Value,
                cancellationToken);

            // If commit fails, it means a different commit advanced the state already; treat as idempotent no-op
            if (!committed)
            {
                return;
            }

            // Additional domain side-effects could be triggered here if needed in the future.
        }
    }
}
