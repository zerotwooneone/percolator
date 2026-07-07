using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Chat;

/// <summary>
/// DTO for a pending group invitation.
/// </summary>
public sealed record PendingGroupInvitationDto
{
    public ConversationId ConversationId { get; init; }
    public ChatPeerId InviterPeerId { get; init; }
    public byte[] CreatorIdentityKey { get; init; } = Array.Empty<byte>();
    public List<byte[]> InitialMembers { get; init; } = new();
    public string? GroupName { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
}

/// <summary>
/// Query interface for pending group invitations.
/// </summary>
public interface IPendingGroupInvitationQueries
{
    /// <summary>
    /// Gets all pending group invitations.
    /// </summary>
    Task<List<PendingGroupInvitationDto>> GetPendingInvitationsAsync(CancellationToken cancellationToken = default);
}
