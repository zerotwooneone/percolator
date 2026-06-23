using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface IRelayRosterQueries
{
    Task<IReadOnlyList<ChatPeerId>> GetMemberPeerIdsAsync(Guid conversationId, CancellationToken cancellationToken);
}
