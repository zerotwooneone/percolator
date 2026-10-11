using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record GroupInvitationResponseDto(
    string InvitationId,
    ChannelId TargetGroupChannelId,
    PublicIdentityId ResponderId,
    bool Accepted,
    ulong RespondedAtUnixMs);
