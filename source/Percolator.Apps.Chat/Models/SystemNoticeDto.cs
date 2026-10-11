using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record SystemNoticeDto(
    ChannelId ChannelId,
    SystemNoticeType NoticeType,
    PublicIdentityId? AffectedIdentity,
    ulong TimestampUnixMs);
