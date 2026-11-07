using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record DeliveryReceiptDto(
    ChannelId ChannelId,
    PublicIdentityId DelivererId,
    PayloadId UpToPayloadId,
    ulong DeliveredAtUnixMs);
