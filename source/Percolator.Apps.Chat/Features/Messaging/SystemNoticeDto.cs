using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record SystemNoticeDto(
    ChannelId ChannelId,
    SystemNoticeType NoticeType,
    PublicIdentityId? AffectedIdentity,
    ulong TimestampUnixMs);
