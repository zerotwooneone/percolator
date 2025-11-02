using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Application.Identity;

namespace Percolator.Application.Apps.Chat
{
    // Provides the next admin sequence number sourced from persisted group admin state.
    internal sealed class AdminSequenceProvider : IAdminSequenceProvider
    {
        private readonly IConversationRepository _conversations;
        private readonly IGroupAdminStateStore _adminStateStore;
        private readonly ActiveIdentityContext _activeIdentityContext;

        public AdminSequenceProvider(
            IConversationRepository conversations,
            IGroupAdminStateStore adminStateStore,
            ActiveIdentityContext activeIdentityContext)
        {
            _conversations = conversations;
            _adminStateStore = adminStateStore;
            _activeIdentityContext = activeIdentityContext;
        }

        public async Task<ulong> NextAsync(Guid groupConversationGuid, CancellationToken ct = default)
        {
            // Resolve the local conversation row for this self identity
            var selfIdentityId = _activeIdentityContext.Identity!.SelfIdentityId;
            var convo = await _conversations.GetByGroupGuidAsync(groupConversationGuid, selfIdentityId).ConfigureAwait(false);
            if (convo is null)
            {
                throw new InvalidOperationException("Group conversation not found for current identity.");
            }

            // Ensure admin state row exists, then read current next sequence without mutating it.
            await _adminStateStore.InitializeIfMissingAsync(convo.Id.Value, ct).ConfigureAwait(false);
            var state = await _adminStateStore.GetAsync(convo.Id.Value, ct).ConfigureAwait(false);
            if (state is null)
            {
                throw new InvalidOperationException("Failed to load group admin state.");
            }
            return state.NextAdminSequenceNumber;
        }
    }
}
