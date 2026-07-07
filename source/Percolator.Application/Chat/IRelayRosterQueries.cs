using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Chat;

public interface IRelayRosterQueries
{
    Task<IReadOnlyList<ChatPeerId>> GetMemberPeerIdsAsync(ConversationId conversationId, CancellationToken cancellationToken);
}
