using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record ReadReceiptDto(
    ChannelId ChannelId,
    PublicIdentityId ReaderId,
    PayloadId UpToPayloadId,
    ulong ReadAtUnixMs);
