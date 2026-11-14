using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record SystemNoticeDto(
    ChannelId ChannelId,
    SystemNoticeType NoticeType,
    PublicIdentityId? AffectedIdentity,
    ulong TimestampUnixMs);
