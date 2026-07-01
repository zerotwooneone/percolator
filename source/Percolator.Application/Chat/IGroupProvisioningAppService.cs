using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Chat;

/// <summary>
/// Application service for provisioning groups with the outbox pattern.
/// This service orchestrates group creation, cryptographic key generation, and member invitation.
/// </summary>
public interface IGroupProvisioningAppService
{
    /// <summary>
    /// Provisions a new group conversation with the specified members.
    /// Generates cryptographic keys, creates the group aggregate, and saves atomically with outbox events.
    /// </summary>
    /// <param name="name">Optional group name.</param>
    /// <param name="inviteePeerIds">List of peer IDs for members to invite.</param>
    /// <param name="relayPeerId">The peer ID of the relay to use for group provisioning.</param>
    /// <param name="selfIdentityId">The local identity ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The conversation ID of the newly created group.</returns>
    Task<ConversationId> ProvisionGroupAsync(
        string? name,
        IReadOnlyList<ChatPeerId> inviteePeerIds,
        ChatPeerId relayPeerId,
        SelfId selfIdentityId,
        CancellationToken cancellationToken = default);
}
