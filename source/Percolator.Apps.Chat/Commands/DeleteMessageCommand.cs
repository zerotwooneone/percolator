using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Commands;

public sealed record DeleteMessageCommand(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId RequestedBy,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route);
