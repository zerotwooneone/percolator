using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record MessageTombstoneDto(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId RequestedBy,
    ulong DeletedAtUnixMs);
