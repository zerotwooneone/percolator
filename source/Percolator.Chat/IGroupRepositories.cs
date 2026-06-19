using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat;

public interface IPendingGroupInvitationRepository
{
    Task AddAsync(PendingGroupInvitation invitation, CancellationToken cancellationToken = default);
    Task UpdateAsync(PendingGroupInvitation invitation, CancellationToken cancellationToken = default);
    Task<PendingGroupInvitation?> GetByConversationIdAsync(ConversationId conversationId, CancellationToken cancellationToken = default);
}
