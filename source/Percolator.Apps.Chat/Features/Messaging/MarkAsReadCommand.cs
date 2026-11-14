using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record MarkAsReadCommand(
    ChannelId ChannelId,
    PublicIdentityId ReaderId,
    PayloadId UpToPayloadId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route);
