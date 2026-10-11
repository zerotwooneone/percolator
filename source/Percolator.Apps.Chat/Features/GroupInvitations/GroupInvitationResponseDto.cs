using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed record GroupInvitationResponseDto(
    InvitationId InvitationId,
    ChannelId TargetGroupChannelId,
    PublicIdentityId ResponderId,
    bool Accepted,
    ulong RespondedAtUnixMs);
