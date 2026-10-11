using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.GroupInvitations;

public sealed record GroupInvitationSummaryReadModel(
    InvitationId InvitationId,
    ChannelId TargetGroupChannelId,
    string GroupName,
    PublicIdentityId InviterId,
    ulong ExpiresAtUnixMs,
    GroupInvitationStatus Status);
