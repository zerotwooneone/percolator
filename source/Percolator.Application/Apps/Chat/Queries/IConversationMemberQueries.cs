using Percolator.Application.Network;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat.Queries;

public interface IConversationMemberQueries
{
    Task<List<GroupMemberWithRouteDto>> GetGroupMembersWithRoutesAsync(
        ConversationId conversationId,
        int selfIdentityId,
        CancellationToken cancellationToken);
}

public sealed record GroupMemberWithRouteDto(
    PeerId PeerId,
    string DisplayName,
    GroupMemberRole Role,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset? RemovedAtUtc,
    RecipientRoute? DeliveryRoute);

public enum GroupMemberRole
{
    Member,
    Admin
}
