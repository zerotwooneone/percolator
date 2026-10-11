using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record GroupInvitationDto(
    string InvitationId,
    ChannelId TargetGroupChannelId,
    string GroupName,
    PublicIdentityId InviterId,
    string? RelayEndpoint,
    ulong ExpiresAtUnixMs);
