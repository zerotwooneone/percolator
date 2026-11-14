using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record ReadReceiptDto(
    ChannelId ChannelId,
    PublicIdentityId ReaderId,
    PayloadId UpToPayloadId,
    ulong ReadAtUnixMs);
