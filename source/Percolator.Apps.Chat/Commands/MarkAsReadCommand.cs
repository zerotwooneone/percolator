using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Commands;

public sealed record MarkAsReadCommand(
    ChannelId ChannelId,
    PublicIdentityId ReaderId,
    PayloadId UpToPayloadId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route);
