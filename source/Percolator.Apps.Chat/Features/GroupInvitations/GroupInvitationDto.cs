using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed record GroupInvitationDto(
    InvitationId InvitationId,
    ChannelId TargetGroupChannelId,
    string GroupName,
    PublicIdentityId InviterId,
    string? RelayEndpoint,
    ulong ExpiresAtUnixMs);
