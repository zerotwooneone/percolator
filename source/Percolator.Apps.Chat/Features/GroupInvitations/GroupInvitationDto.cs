using System.Collections.Generic;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed record GroupInvitationDto(
    InvitationId InvitationId,
    ChannelId TargetGroupChannelId,
    string GroupName,
    PublicIdentityId InviterId,
    string? RelayEndpoint,
    ulong ExpiresAtUnixMs,
    IReadOnlyList<PublicIdentityId>? Members = null);
