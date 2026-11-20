using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed record GroupInvitationResponseDto(
    InvitationId InvitationId,
    ChannelId TargetGroupChannelId,
    PublicIdentityId ResponderId,
    bool Accepted,
    ulong RespondedAtUnixMs);
