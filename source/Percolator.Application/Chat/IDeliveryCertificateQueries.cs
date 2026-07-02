using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

public interface IDeliveryCertificateQueries
{
    /// <summary>
    /// Gets all active relay assignments for local identities.
    /// Returns distinct pairs of (SelfId, RelayPeerId) for all groups where the local identity is an active member.
    /// </summary>
    Task<IReadOnlyList<(ChatSelfId SelfId, ChatPeerId RelayPeerId)>> GetActiveRelayAssignmentsAsync(CancellationToken ct);
}
