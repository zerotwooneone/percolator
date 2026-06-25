using Percolator.Contracts;

namespace Percolator.Application.Chat;

/// <summary>
/// Handler for processing incoming group invitations.
/// </summary>
public interface IGroupInviteHandler
{
    /// <summary>
    /// Handles an incoming group invite message.
    /// Parses the protobuf, converts domain types, persists state, and initializes the group conversation.
    /// </summary>
    /// <param name="invite">The group invite protobuf message.</param>
    /// <param name="selfIdentityId">The local identity ID.</param>
    /// <param name="sourceDeviceId">The sender's device ID (from InternalEnvelope context).</param>
    /// <param name="ct">Cancellation token.</param>
    Task HandleGroupInviteAsync(GroupInvite invite, int selfIdentityId, uint sourceDeviceId, CancellationToken ct = default);
}
